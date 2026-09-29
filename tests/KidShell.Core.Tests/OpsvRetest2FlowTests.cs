using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Runtime;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security;
using KidShell.Core.Security.AppControl;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The six OPSV retest 2 findings, as whole flows rather than as units.
///
/// Every one of them was a correct component reached through a path that undid
/// it: storage that could not be written, a fallback that reopened, a backup
/// that was older than the thing it recovered, a timeout driven by a silent
/// event, an identity dropped one method after it was read, a throttle that
/// lived only as long as the process.
///
/// Unit tests found none of them, because each unit was right. These follow the
/// path.
/// </summary>
public class OpsvRetest2FlowTests
{
    private static readonly IRuntimeEnvironment Production =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    // ----------------------------------------------------------- flow 1
    // Hostile user JSON + missing expected protected policy => fail closed.

    [Fact]
    public void Flow_a_hostile_user_file_cannot_replace_a_missing_protected_policy()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        ProtectedConfigurationStore Store() => new(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger);

        // A real parent sets the machine up.
        var configured = KidShellConfiguration.CreateDefault();
        configured.ScreenTime.IsEnabled = true;
        configured.ScreenTime.WeekdayMinutes = 30;
        Assert.True(Store().Save(configured));

        // The protected policy is removed, and a hostile file is left where the
        // child can write one.
        vault.Delete(ProtectedDocument.ParentPolicy);

        File.WriteAllText(path, """
        {
          "schemaVersion": 2,
          "parentPin": { "hash": "hostile", "salt": "hostile", "iterations": 210000 },
          "screenTime": { "isEnabled": false, "weekdayMinutes": 1440 }
        }
        """);

        var state = new AppStateService(Store(), logger);
        var status = state.Initialize();

        Assert.Equal(ConfigurationLoadStatus.Failed, status);
        Assert.NotEqual("hostile", state.Current.ParentPin.Hash);
        Assert.NotEqual(1440, state.Current.ScreenTime.WeekdayMinutes);
    }

    // ----------------------------------------------------------- flow 2
    // Store discovery -> add -> persist -> reload -> AppLocker rule.

    [Fact]
    public void Flow_a_discovered_store_app_reaches_an_applocker_rule()
    {
        using var dir = new TempDirectory();

        var discovered = new DiscoveredApplication
        {
            Key = "calculator",
            DisplayName = "Miniräknare",
            Kind = ApplicationKind.Packaged,
            Source = DiscoverySource.PackagedApp,
            Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            Aumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
        };

        var approved = new KidAppDefinition
        {
            Id = "calc",
            DisplayName = discovered.DisplayName,
            ProgramName = discovered.DisplayName,
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = discovered.LaunchTarget,
            Publisher = discovered.Publisher,
            PackageFamilyName = "Microsoft.WindowsCalculator_8wekyb3d8bbwe"
        };

        var store = new JsonConfigurationStore(
            Path.Combine(dir.Path, "kidshell.config.json"), new RecordingLogger());

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps.Clear();
        configuration.Apps.Add(approved);
        store.Save(configuration);

        var policy = AppControlPolicyBuilder.Build(
            store.Load().Configuration,
            ApplicationProfileLibrary.Default,
            @"C:\Program Files\KidShell\KidShell.exe",
            "S-1-5-21-0-0-0-1001");

        var rule = Assert.Single(policy.ApplicationRules, r => r.Collection == RuleCollection.Appx);

        Assert.Equal(RuleStrategy.Publisher, rule.Strategy);
        Assert.Contains("Microsoft Corporation", rule.Value);
        Assert.DoesNotContain(policy.Warnings, w => w.Code == "packaged-app-without-identity");
    }

    // ----------------------------------------------------------- flow 3
    // Screen-time writer failure -> restart -> no refund.

    [Fact]
    public void Flow_a_writer_failure_and_a_restart_never_refund_time()
    {
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        var logger = new RecordingLogger();

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = 60;

        var state = new StubState(configuration);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

        IScreenTimeStateStore Store() => new ProtectedScreenTimeStateStore(
            new InMemoryScreenTimeStateStore(), vault, vault, Production, logger);

        var first = new ScreenTimeEngine(state, Store(), logger, time);

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            first.Tick();
        }

        var used = first.State.UsedSeconds;
        Assert.True(used >= 60);

        // The broker stops working, and the process goes away without closing.
        vault.RefuseWrites = true;

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            first.Tick();
        }

        vault.RefuseWrites = false;

        var restarted = new ScreenTimeEngine(state, Store(), logger, time);

        Assert.True(restarted.State.UsedSeconds >= used, "a write failure refunded time");
        Assert.True(restarted.Evaluate().IsBlocked, "an unclosed session was treated as a clean stop");
    }

    // ----------------------------------------------------------- flow 4
    // Parent Mode, no screen-time changes at all, timeout => locked.

    [Fact]
    public void Flow_parent_mode_relocks_with_no_screen_time_activity()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 19, 0, 0, TimeSpan.Zero));
        var session = new ParentSession(new RecordingLogger(), time);

        using var scheduler = new ManualPeriodicScheduler();
        scheduler.Start(ParentSession.HeartbeatInterval, session.Evaluate);

        session.Begin();

        // Twenty minutes, and nothing about screen time happens at all - no
        // engine, no coordinator, no event.
        for (var i = 0; i < 40; i++)
        {
            time.Advance(ParentSession.HeartbeatInterval);
            scheduler.Advance(ParentSession.HeartbeatInterval);
        }

        Assert.False(session.IsUnlocked);
        Assert.Equal(ParentSessionEndReason.Inactivity, session.LastEndReason);
    }

    // ----------------------------------------------------------- flow 5
    // PIN throttle -> restart -> still throttled.

    [Fact]
    public void Flow_the_pin_throttle_survives_a_restart()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 19, 0, 0, TimeSpan.Zero));
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        var configuration = KidShellConfiguration.CreateDefault();
        var logger = new RecordingLogger();

        ParentPinService Service() => new(
            new StubState(configuration), Production, logger, time,
            new ProtectedPinThrottleStore(vault, vault, logger, time));

        var first = Service();

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            first.Verify("000000");
        }

        Assert.Equal(PinVerificationResult.Throttled, first.Verify("000000"));
        Assert.Equal(PinVerificationResult.Throttled, Service().Verify("000000"));
    }

    // ----------------------------------------------------------- flow 6
    // The child cannot direct-write protected state; the broker path can.

    [Fact]
    public void Flow_the_child_cannot_write_protected_state_but_the_broker_can()
    {
        using var dir = new TempDirectory();
        var protectedDirectory = Path.Combine(dir.Path, "policy");
        Directory.CreateDirectory(protectedDirectory);

        var logger = new RecordingLogger();

        // The child's side. A reader, and nothing on it can write - the type
        // check is the assertion, because the whole point is that the
        // capability is absent rather than merely unused.
        var reader = new FileSystemProtectedStateReader(protectedDirectory, logger);

        Assert.DoesNotContain(
            typeof(IProtectedStateReader).GetMethods(),
            m => m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase));

        // A directory this process can write is reported as wrongly
        // permissioned, which is what stops it being trusted.
        Assert.Equal(ProtectedStoreStatus.PermissionsWrong, reader.Probe().Status);

        // The privileged side, exercised through a test double standing in for
        // the elevated helper.
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        Assert.True(vault.SaveParentPolicy("""{"schemaVersion":1}""").Success);
        Assert.NotNull(vault.Read(ProtectedDocument.ParentPolicy));
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
