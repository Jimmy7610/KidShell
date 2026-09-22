using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.ScreenTime;
using KidShell.Core.Sessions;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>A launcher that records rather than starting anything.</summary>
internal sealed class RecordingLauncher : IAppLauncher
{
    private readonly LaunchStatus _status;

    public RecordingLauncher(LaunchStatus status = LaunchStatus.Success) => _status = status;

    public List<string> Launched { get; } = [];

    public LaunchResult Launch(KidAppDefinition app)
    {
        Launched.Add(app.Id);

        return _status switch
        {
            LaunchStatus.Success => LaunchResult.Success(app),
            LaunchStatus.NotFound => LaunchResult.NotFound(app),
            LaunchStatus.NotConfigured => LaunchResult.NotConfigured(app),
            _ => LaunchResult.Failed(app)
        };
    }
}

public class ChildSessionTests
{
    private static (ChildSessionManager Session, RecordingLauncher Launcher, ScreenTimeEngine ScreenTime, FakeTimeProvider Time)
        Create(TempDirectory dir, Action<ScreenTimeSettings>? configure = null, DateTimeOffset? start = null)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 60;
        configure?.Invoke(draft.ScreenTime);
        state.Commit(draft);

        var time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        var screenTime = new ScreenTimeEngine(state, new InMemoryScreenTimeStateStore(), logger, time);
        var launcher = new RecordingLauncher();

        return (new ChildSessionManager(state, launcher, screenTime, logger, time), launcher, screenTime, time);
    }

    private static KidAppDefinition App(string id = "paint", string path = @"C:\Windows\System32\mspaint.exe", bool enabled = true) =>
        new() { Id = id, DisplayName = id, ExecutablePath = path, IsEnabled = enabled };

    // ------------------------------------------------ launching

    [Fact]
    public void An_allowed_app_starts_and_becomes_active()
    {
        using var dir = new TempDirectory();
        var (session, launcher, _, _) = Create(dir);

        var result = session.Launch(App());

        Assert.Equal(LaunchStatus.Success, result.Status);
        Assert.Equal(ChildSessionState.InApp, session.State);
        Assert.Equal("paint", session.ActiveApp?.Id);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public void A_disabled_app_is_refused_without_being_started()
    {
        using var dir = new TempDirectory();
        var (session, launcher, _, _) = Create(dir);

        var decision = session.CanLaunch(App(enabled: false));

        Assert.False(decision.Allowed);
        Assert.Equal(LaunchRefusal.NotAllowed, decision.Refusal);

        session.Launch(App(enabled: false));
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void An_unconfigured_app_is_refused_with_a_friendly_reason()
    {
        using var dir = new TempDirectory();
        var (session, launcher, _, _) = Create(dir);

        var result = session.Launch(App(path: ""));

        Assert.Equal(LaunchStatus.NotConfigured, result.Status);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public void A_failed_launch_leaves_the_child_at_home()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        var screenTime = new ScreenTimeEngine(state, new InMemoryScreenTimeStateStore(), logger, time);
        var session = new ChildSessionManager(state, new RecordingLauncher(LaunchStatus.NotFound), screenTime, logger, time);

        session.Launch(App());

        Assert.Equal(ChildSessionState.Home, session.State);
        Assert.Null(session.ActiveApp);
    }

    // ------------------------------------------------ screen time gating

    [Fact]
    public void Expired_screen_time_refuses_a_launch_before_starting_anything()
    {
        using var dir = new TempDirectory();
        var (session, launcher, screenTime, time) = Create(dir, s => s.WeekdayMinutes = 5);

        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();

        var result = session.Launch(App());

        // Refused, not failed: the child is told why rather than watching
        // something not start.
        Assert.Empty(launcher.Launched);
        Assert.Equal(LaunchStatus.Blocked, result.Status);
        Assert.Equal(ChildSessionState.TimeUp, session.State);
    }

    [Fact]
    public void Being_outside_allowed_hours_refuses_a_launch()
    {
        using var dir = new TempDirectory();
        var (session, launcher, _, _) = Create(
            dir,
            s =>
            {
                s.RestrictHours = true;
                s.AllowedFromHour = 7;
                s.AllowedUntilHour = 20;
            },
            start: new DateTimeOffset(2026, 3, 11, 23, 0, 0, TimeSpan.Zero));

        session.Launch(App());

        Assert.Empty(launcher.Launched);
        Assert.Equal(ChildSessionState.OutsideHours, session.State);
    }

    [Fact]
    public void Time_running_out_during_an_app_is_noticed_on_return()
    {
        using var dir = new TempDirectory();
        var (session, _, screenTime, time) = Create(dir, s => s.WeekdayMinutes = 5);

        session.Launch(App());
        Assert.Equal(ChildSessionState.InApp, session.State);

        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();

        session.ReturnToHome();

        Assert.Equal(ChildSessionState.TimeUp, session.State);
    }

    [Fact]
    public void Refresh_moves_the_child_to_the_locked_screen_promptly()
    {
        using var dir = new TempDirectory();
        var (session, _, screenTime, time) = Create(dir, s => s.WeekdayMinutes = 5);

        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();

        session.Refresh();

        // The child should not have to try launching something to find out.
        Assert.Equal(ChildSessionState.TimeUp, session.State);
    }

    [Fact]
    public void An_extension_lets_the_child_continue()
    {
        using var dir = new TempDirectory();
        var (session, launcher, screenTime, time) = Create(dir, s => s.WeekdayMinutes = 5);

        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        screenTime.Tick();
        session.Refresh();
        Assert.Equal(ChildSessionState.TimeUp, session.State);

        screenTime.GrantExtension(30);
        session.Refresh();

        Assert.Equal(ChildSessionState.Home, session.State);
        Assert.Equal(LaunchStatus.Success, session.Launch(App()).Status);
        Assert.Single(launcher.Launched);
    }

    // ------------------------------------------------ state changes

    [Fact]
    public void State_changes_are_raised_once_each()
    {
        using var dir = new TempDirectory();
        var (session, _, _, _) = Create(dir);

        var states = new List<ChildSessionState>();
        session.StateChanged += (_, s) => states.Add(s);

        session.Launch(App());
        session.ReturnToHome();

        Assert.Equal([ChildSessionState.InApp, ChildSessionState.Home], states);
    }

    [Fact]
    public void Returning_home_more_than_once_does_not_raise_repeatedly()
    {
        using var dir = new TempDirectory();
        var (session, _, _, _) = Create(dir);

        session.Launch(App());

        var raised = 0;
        session.StateChanged += (_, _) => raised++;

        session.ReturnToHome();
        session.ReturnToHome();

        Assert.Equal(1, raised);
    }

    // ------------------------------------------------ privacy

    [Fact]
    public void A_session_records_only_the_app_and_a_duration()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        var screenTime = new ScreenTimeEngine(state, new InMemoryScreenTimeStateStore(), logger, time);
        var session = new ChildSessionManager(state, new RecordingLauncher(), screenTime, logger, time);

        session.Launch(App("paint", @"C:\Users\Lucas\Documents\secret-drawing.png"));
        time.Advance(TimeSpan.FromMinutes(12));
        session.ReturnToHome();

        var log = string.Join("\n", logger.Messages);

        // The app id and a duration, and nothing about what the child did.
        Assert.Contains("paint", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-drawing", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Documents", log, StringComparison.Ordinal);
    }
}

/// <summary>Ending a session.</summary>
public class SessionControllerTests
{
    [Fact]
    public async Task The_development_controller_performs_nothing()
    {
        var controller = new DevelopmentSessionController(new RecordingLogger());

        var result = await controller.PerformAsync(SessionAction.SignOut);

        // A developer who loses their session to a button they were testing
        // has lost their work.
        Assert.True(controller.IsSimulated);
        Assert.False(result.Performed);
        Assert.Contains("Simulerat", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_records_what_would_have_happened()
    {
        var controller = new DevelopmentSessionController(new RecordingLogger());

        await controller.PerformAsync(SessionAction.CloseKidShell);
        await controller.PerformAsync(SessionAction.SignOut);
        await controller.PerformAsync(SessionAction.Lock);

        Assert.Equal(
            [SessionAction.CloseKidShell, SessionAction.SignOut, SessionAction.Lock],
            controller.Requested);
    }

    [Fact]
    public void The_simulated_controller_is_the_only_implementation()
    {
        // Signing a Windows account out is a machine action and belongs behind
        // the same boundary as everything else.
        var implementations = typeof(ISessionController).Assembly
            .GetTypes()
            .Where(t => typeof(ISessionController).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .ToArray();

        var only = Assert.Single(implementations);
        Assert.Equal(typeof(DevelopmentSessionController), only);
    }

    [Theory]
    [InlineData(SessionAction.CloseKidShell)]
    [InlineData(SessionAction.SignOut)]
    [InlineData(SessionAction.Lock)]
    public void Every_action_has_parent_facing_wording(SessionAction action) =>
        Assert.False(string.IsNullOrWhiteSpace(SessionActionResult.Describe(action)));
}
