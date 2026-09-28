using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Records every launch it is asked for and starts nothing.
/// </summary>
internal sealed class CountingLauncher : IAppLauncher
{
    public int Count { get; private set; }

    public List<string> Launched { get; } = [];

    public LaunchResult Launch(KidAppDefinition app)
    {
        Count++;
        Launched.Add(app.Id);
        return LaunchResult.Success(app);
    }
}

/// <summary>A coordinator whose answer a test sets directly.</summary>
internal sealed class StubScreenTimeCoordinator : IScreenTimeCoordinator
{
    public bool Allowed { get; set; } = true;

    public int CanLaunchCalls { get; private set; }

    public ScreenTimeStatusView Current { get; set; } = new()
    {
        Snapshot = new ScreenTimeSnapshot
        {
            Status = ScreenTimeStatus.NotLimited,
            Used = TimeSpan.Zero,
            Allowance = TimeSpan.Zero
        }
    };

    public event EventHandler<ScreenTimeStatusView>? Changed;

    public void Start() { }

    public void Stop() { }

    public bool CanLaunch()
    {
        CanLaunchCalls++;
        return Allowed;
    }

    public void AcknowledgeWarning(int minutes) { }

    public void Refresh() => Changed?.Invoke(this, Current);
}

/// <summary>
/// EXTERNAL AUDIT FINDING 02 — a launch path that skipped screen time.
///
/// Both callers that start an app do check first, and both were correct by
/// the time this pass began. That is still only a convention: it holds
/// because two authors remembered, and it holds until somebody writes a
/// third caller. The audit found precisely that shape of bug.
///
/// So the gate moved to the one point every launch passes through, and these
/// tests hold it there. The question they ask is the only one that matters:
/// when screen time says no, how many times was anything actually started?
/// </summary>
public class ScreenTimeLaunchGateTests
{
    private static (ScreenTimeGuardedLauncher Guarded, CountingLauncher Inner, StubScreenTimeCoordinator Time)
        Build(bool allowed)
    {
        var inner = new CountingLauncher();
        var screenTime = new StubScreenTimeCoordinator { Allowed = allowed };
        var guarded = new ScreenTimeGuardedLauncher(inner, screenTime, new RecordingLogger());

        return (guarded, inner, screenTime);
    }

    private static KidAppDefinition App(string id = "paint") => new()
    {
        Id = id,
        DisplayName = "Paint",
        ProgramName = "Paint",
        ExecutablePath = "mspaint.exe"
    };

    /// <summary>The claim, stated as plainly as it can be.</summary>
    [Fact]
    public void Nothing_starts_when_screen_time_says_no()
    {
        var (guarded, inner, _) = Build(allowed: false);

        var result = guarded.Launch(App());

        Assert.Equal(0, inner.Count);
        Assert.Equal(LaunchStatus.Blocked, result.Status);
    }

    [Fact]
    public void Nothing_starts_however_many_times_it_is_asked()
    {
        var (guarded, inner, _) = Build(allowed: false);

        for (var i = 0; i < 25; i++)
        {
            guarded.Launch(App($"app-{i}"));
        }

        Assert.Equal(0, inner.Count);
        Assert.Empty(inner.Launched);
    }

    [Fact]
    public void A_launch_goes_through_when_screen_time_allows_it()
    {
        var (guarded, inner, _) = Build(allowed: true);

        var result = guarded.Launch(App());

        Assert.Equal(1, inner.Count);
        Assert.Equal(LaunchStatus.Success, result.Status);
    }

    /// <summary>
    /// The race the audit was really about: the grid was drawn while there was
    /// still time, and the allowance ran out before the child tapped.
    /// </summary>
    [Fact]
    public void Time_running_out_between_drawing_and_tapping_still_blocks()
    {
        var (guarded, inner, screenTime) = Build(allowed: true);

        // Drawn while allowed.
        Assert.Equal(LaunchStatus.Success, guarded.Launch(App("first")).Status);
        Assert.Equal(1, inner.Count);

        // The allowance runs out.
        screenTime.Allowed = false;

        Assert.Equal(LaunchStatus.Blocked, guarded.Launch(App("second")).Status);
        Assert.Equal(1, inner.Count);
    }

    /// <summary>
    /// Asked every time, not cached. A gate that remembers yesterday's answer
    /// is not a gate.
    /// </summary>
    [Fact]
    public void The_question_is_asked_on_every_launch()
    {
        var (guarded, _, screenTime) = Build(allowed: true);

        guarded.Launch(App("a"));
        guarded.Launch(App("b"));
        guarded.Launch(App("c"));

        Assert.Equal(3, screenTime.CanLaunchCalls);
    }

    /// <summary>
    /// A parent granting more time has to work immediately: the child is
    /// standing there.
    /// </summary>
    [Fact]
    public void A_parent_granting_time_lets_the_next_launch_through()
    {
        var (guarded, inner, screenTime) = Build(allowed: false);

        Assert.Equal(LaunchStatus.Blocked, guarded.Launch(App()).Status);
        Assert.Equal(0, inner.Count);

        screenTime.Allowed = true;

        Assert.Equal(LaunchStatus.Success, guarded.Launch(App()).Status);
        Assert.Equal(1, inner.Count);
    }

    // ------------------------------------------------ through the real engine

    /// <summary>
    /// The same guarantee against the real coordinator and the real engine,
    /// so it does not rest on a stub agreeing with itself.
    /// </summary>
    private static (ScreenTimeGuardedLauncher Guarded, CountingLauncher Inner, ScreenTimeEngine Engine, FakeTimeProvider Time)
        BuildReal(TempDirectory dir, Action<ScreenTimeSettings>? configure = null)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 60;
        draft.ScreenTime.WeekendMinutes = 120;
        configure?.Invoke(draft.ScreenTime);
        state.Commit(draft);

        // A Wednesday.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        var engine = new ScreenTimeEngine(state, new InMemoryScreenTimeStateStore(), logger, time);
        var inner = new CountingLauncher();

        var coordinator = new EngineBackedCoordinator(engine);
        return (new ScreenTimeGuardedLauncher(inner, coordinator, logger), inner, engine, time);
    }

    /// <summary>Adapts the engine to the coordinator interface for these tests.</summary>
    private sealed class EngineBackedCoordinator(ScreenTimeEngine engine) : IScreenTimeCoordinator
    {
        public ScreenTimeStatusView Current => new() { Snapshot = engine.Evaluate() };

        public event EventHandler<ScreenTimeStatusView>? Changed;

        public void Start() { }

        public void Stop() { }

        // The same question the production coordinator asks.
        public bool CanLaunch() => !engine.Evaluate().IsBlocked;

        public void AcknowledgeWarning(int minutes) { }

        public void Refresh() => Changed?.Invoke(this, Current);
    }

    private static void Spend(ScreenTimeEngine engine, FakeTimeProvider time, int minutes)
    {
        for (var spent = 0; spent < minutes; spent += 4)
        {
            time.Advance(TimeSpan.FromMinutes(Math.Min(4, minutes - spent)));
            engine.Tick();
        }
    }

    [Fact]
    public void An_expired_allowance_starts_nothing()
    {
        using var dir = new TempDirectory();
        var (guarded, inner, engine, time) = BuildReal(dir);

        Spend(engine, time, 60);
        Assert.Equal(ScreenTimeStatus.Expired, engine.Evaluate().Status);

        Assert.Equal(LaunchStatus.Blocked, guarded.Launch(App()).Status);
        Assert.Equal(0, inner.Count);
    }

    [Fact]
    public void A_warning_does_not_stop_a_launch()
    {
        using var dir = new TempDirectory();
        var (guarded, inner, engine, time) = BuildReal(dir);

        Spend(engine, time, 56);

        Assert.Equal(ScreenTimeStatus.Warning, engine.Evaluate().Status);
        Assert.Equal(LaunchStatus.Success, guarded.Launch(App()).Status);
        Assert.Equal(1, inner.Count);
    }

    [Fact]
    public void An_unlimited_configuration_starts_whatever_is_asked()
    {
        using var dir = new TempDirectory();
        var (guarded, inner, engine, time) = BuildReal(dir, s => s.IsEnabled = false);

        Spend(engine, time, 300);

        Assert.Equal(ScreenTimeStatus.NotLimited, engine.Evaluate().Status);
        Assert.Equal(LaunchStatus.Success, guarded.Launch(App()).Status);
        Assert.Equal(1, inner.Count);
    }

    [Fact]
    public void Outside_the_allowed_hours_nothing_starts()
    {
        using var dir = new TempDirectory();
        var (guarded, inner, engine, _) = BuildReal(dir, s =>
        {
            s.RestrictHours = true;
            s.AllowedFromHour = 14;
            s.AllowedUntilHour = 18;
        });

        // The harness starts at 10:00, which is before the window opens.
        Assert.Equal(ScreenTimeStatus.OutsideAllowedHours, engine.Evaluate().Status);
        Assert.Equal(LaunchStatus.Blocked, guarded.Launch(App()).Status);
        Assert.Equal(0, inner.Count);
    }

    [Fact]
    public void An_extension_granted_by_a_parent_lets_the_child_start_something()
    {
        using var dir = new TempDirectory();
        var (guarded, inner, engine, time) = BuildReal(dir);

        Spend(engine, time, 60);
        Assert.Equal(LaunchStatus.Blocked, guarded.Launch(App()).Status);
        Assert.Equal(0, inner.Count);

        engine.GrantExtension(15);

        Assert.Equal(LaunchStatus.Success, guarded.Launch(App()).Status);
        Assert.Equal(1, inner.Count);
    }

    /// <summary>
    /// Usage survives a restart, so the allowance cannot be refreshed by
    /// closing and reopening KidShell.
    /// </summary>
    [Fact]
    public void Restarting_does_not_return_the_allowance()
    {
        using var dir = new TempDirectory();
        var store = new InMemoryScreenTimeStateStore();

        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 60;
        state.Commit(draft);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        var first = new ScreenTimeEngine(state, store, logger, time);
        Spend(first, time, 60);

        // A second engine, same stored state, same day.
        var second = new ScreenTimeEngine(
            state, store, logger,
            new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero)));

        var inner = new CountingLauncher();
        var guarded = new ScreenTimeGuardedLauncher(inner, new EngineBackedCoordinator(second), logger);

        Assert.Equal(ScreenTimeStatus.Expired, second.Evaluate().Status);
        Assert.Equal(LaunchStatus.Blocked, guarded.Launch(App()).Status);
        Assert.Equal(0, inner.Count);
    }
}
