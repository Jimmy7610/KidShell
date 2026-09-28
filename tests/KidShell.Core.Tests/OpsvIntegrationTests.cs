using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Runtime;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The OPSV retest found the same shape of defect four times: a correct class,
/// a thorough test suite for it, and a production application that used a
/// different path. Protected storage was written and registered nowhere. The
/// packaged-app model existed in discovery and was thrown away at the boundary.
///
/// So these tests assemble the real types together and ask what the composed
/// product does, rather than what each part does alone. They are the
/// behavioural half of the composition check in tools/check-composition.ps1,
/// which reads the container itself - KidShell.App is a WinUI project and
/// cannot be referenced from a test assembly.
/// </summary>
public class OpsvIntegrationTests
{
    private static readonly IRuntimeEnvironment Production =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    // ------------------------------------------------- policy end to end

    [Fact]
    public void A_parent_saving_settings_writes_policy_where_the_child_cannot_reach_it()
    {
        using var dir = new TempDirectory();
        var logger = new RecordingLogger();
        var childFile = Path.Combine(dir.Path, "kidshell.config.json");
        var vault = new InMemoryProtectedPolicyStore();

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(childFile, logger), vault, Production, logger);

        var state = new AppStateService(store, logger);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 30;
        draft.Child.Name = "Lucas";

        Assert.True(state.Commit(draft));

        // The decision went to the protected store.
        Assert.NotNull(vault.Read(ProtectedConfigurationStore.PolicyDocumentName));

        // And a reload gets it from there, not from the file the child owns.
        var reloaded = new AppStateService(
            new ProtectedConfigurationStore(
                new JsonConfigurationStore(childFile, logger), vault, Production, logger),
            logger);

        reloaded.Initialize();

        Assert.Equal(30, reloaded.Current.ScreenTime.WeekdayMinutes);
        Assert.Equal("Lucas", reloaded.Current.Child.Name);
    }

    [Fact]
    public void Tampering_with_the_child_writable_file_does_not_change_the_rules()
    {
        using var dir = new TempDirectory();
        var logger = new RecordingLogger();
        var childFile = Path.Combine(dir.Path, "kidshell.config.json");
        var vault = new InMemoryProtectedPolicyStore();

        ProtectedConfigurationStore Store() => new(
            new JsonConfigurationStore(childFile, logger), vault, Production, logger);

        var state = new AppStateService(Store(), logger);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 30;
        state.Commit(draft);

        // The whole point of the boundary, exercised.
        File.WriteAllText(childFile,
            File.ReadAllText(childFile).Replace("\"weekdayMinutes\": 30", "\"weekdayMinutes\": 600"));

        var reloaded = new AppStateService(Store(), logger);
        reloaded.Initialize();

        Assert.Equal(30, reloaded.Current.ScreenTime.WeekdayMinutes);
    }

    // ------------------------------------------- launcher end to end

    [Fact]
    public void Every_launch_passes_the_screen_time_gate()
    {
        // The guarded decorator is what production resolves. Composed here the
        // same way, so a launch that skipped the gate would show up.
        var runner = new RecordingRunner();
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = 30;

        var state = new StubState(configuration);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));
        var engine = new ScreenTimeEngine(state, new InMemoryScreenTimeStateStore(), new RecordingLogger(), time);
        using var coordinator = new ScreenTimeCoordinator(engine, state, new RecordingLogger());

        IAppLauncher launcher = new ScreenTimeGuardedLauncher(
            new AppLauncher(new WindowsExecutableResolver(), runner, new RecordingLogger()),
            coordinator,
            new RecordingLogger());

        var app = new KidAppDefinition
        {
            Id = "paint",
            DisplayName = "Rita",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = "Microsoft.Paint_8wekyb3d8bbwe!App"
        };

        Assert.Equal(LaunchStatus.Success, launcher.Launch(app).Status);

        // Spend the allowance.
        for (var i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            engine.Tick();
        }

        coordinator.Refresh();

        var blocked = launcher.Launch(app);

        Assert.NotEqual(LaunchStatus.Success, blocked.Status);
        Assert.Equal(1, runner.StartCount);
    }

    // -------------------------------------- counter end to end

    [Fact]
    public void The_counter_survives_a_restart_through_the_protected_store()
    {
        var vault = new InMemoryProtectedPolicyStore();
        var logger = new RecordingLogger();

        IScreenTimeStateStore Store() => new ProtectedScreenTimeStateStore(
            new InMemoryScreenTimeStateStore(), vault, Production, logger);

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = 60;

        var state = new StubState(configuration);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));

        var first = new ScreenTimeEngine(state, Store(), logger, time);

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            first.Tick();
        }

        var used = first.State.UsedSeconds;
        Assert.True(used > 0);

        var second = new ScreenTimeEngine(state, Store(), logger, time);

        Assert.Equal(used, second.State.UsedSeconds);
        Assert.False(second.IsUsageUnknown);
    }

    [Fact]
    public void Deleting_the_protected_counter_does_not_hand_back_the_afternoon()
    {
        var vault = new InMemoryProtectedPolicyStore();
        var logger = new RecordingLogger();

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = 60;

        var state = new StubState(configuration);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));

        var store = new ProtectedScreenTimeStateStore(
            new InMemoryScreenTimeStateStore(), vault, Production, logger);

        var engine = new ScreenTimeEngine(state, store, logger, time);

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            engine.Tick();
        }

        // Damage it the way a determined child would.
        vault.Corrupt(ProtectedScreenTimeStateStore.DocumentName);

        var afterRestart = new ScreenTimeEngine(state, store, logger, time);

        Assert.True(afterRestart.IsUsageUnknown);
        Assert.True(afterRestart.Evaluate().IsBlocked);
    }

    // ------------------------------------------------------------ doubles

    private sealed class RecordingRunner : IProcessRunner
    {
        public int StartCount { get; private set; }

        public void Start(string fileName, string arguments, bool useShellExecute) => StartCount++;
    }

    private sealed class StubState : IAppStateService
    {
        public StubState(KidShellConfiguration configuration) => Current = configuration;

        public KidShellConfiguration Current { get; private set; }

        public ConfigurationLoadStatus LoadStatus => ConfigurationLoadStatus.Loaded;

        public string? LoadDetail => null;

        public string ConfigurationFilePath => "(in-memory)";

        public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

        public ConfigurationLoadStatus Initialize() => ConfigurationLoadStatus.Loaded;

        public KidShellConfiguration CreateDraft() => Current.Clone();

        public bool Commit(KidShellConfiguration draft)
        {
            Current = draft;
            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(Current));
            return true;
        }

        public bool SaveCurrent() => true;
    }
}
