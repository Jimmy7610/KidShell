using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;
using KidShell.Core.ScreenTime;

namespace KidShell.Core.Sessions;

/// <summary>What the child is doing right now.</summary>
public enum ChildSessionState
{
    /// <summary>On the KidShell home screen.</summary>
    Home = 0,

    /// <summary>An app was started and is believed to be running.</summary>
    InApp = 1,

    /// <summary>Screen time ran out; the locked screen is showing.</summary>
    TimeUp = 2,

    /// <summary>Outside the hours the child may use the computer.</summary>
    OutsideHours = 3
}

/// <summary>Why a launch was refused.</summary>
public enum LaunchRefusal
{
    None = 0,
    ScreenTimeExpired = 1,
    OutsideAllowedHours = 2,
    NotConfigured = 3,
    NotAllowed = 4
}

public sealed record SessionLaunchDecision(bool Allowed, LaunchRefusal Refusal, string Message);

/// <summary>
/// Owns what the child is doing.
///
/// It knows which app is active, decides whether a launch may proceed, and
/// returns to the home screen when an app exits. Screen time is consulted
/// before every launch, which is what makes the limit mean something at the
/// application level even with no Windows restriction in place.
///
/// PRIVACY: it records which app was started and for how long, and nothing
/// else. No keystrokes, no window titles, no file names, no page contents.
/// "How long was the computer used" is a duration; anything more would be
/// surveillance of a child, which this product does not do.
/// </summary>
public sealed class ChildSessionManager
{
    private readonly IAppStateService _state;
    private readonly IAppLauncher _launcher;
    private readonly ScreenTimeEngine _screenTime;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;

    private DateTimeOffset? _appStartedAt;

    public ChildSessionManager(
        IAppStateService state,
        IAppLauncher launcher,
        ScreenTimeEngine screenTime,
        IKidShellLogger logger,
        TimeProvider? time = null)
    {
        _state = state;
        _launcher = launcher;
        _screenTime = screenTime;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public ChildSessionState State { get; private set; } = ChildSessionState.Home;

    /// <summary>The app the child is in, or null on the home screen.</summary>
    public KidAppDefinition? ActiveApp { get; private set; }

    /// <summary>Raised when the state changes, so the shell can follow.</summary>
    public event EventHandler<ChildSessionState>? StateChanged;

    /// <summary>
    /// Whether an app may be launched right now.
    ///
    /// Checked before launching rather than after, so the child is told why
    /// instead of watching something fail to start.
    /// </summary>
    public SessionLaunchDecision CanLaunch(KidAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.IsEnabled)
        {
            return new SessionLaunchDecision(false, LaunchRefusal.NotAllowed,
                "Den appen är inte påslagen.");
        }

        var screenTime = _screenTime.Evaluate();

        if (screenTime.Status == ScreenTimeStatus.OutsideAllowedHours)
        {
            return new SessionLaunchDecision(false, LaunchRefusal.OutsideAllowedHours,
                "Datorn är vilande just nu.");
        }

        if (screenTime.Status == ScreenTimeStatus.Expired)
        {
            return new SessionLaunchDecision(false, LaunchRefusal.ScreenTimeExpired,
                "Skärmtiden är slut för idag.");
        }

        if (string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            return new SessionLaunchDecision(false, LaunchRefusal.NotConfigured,
                $"{app.DisplayName} är inte konfigurerat ännu.");
        }

        return new SessionLaunchDecision(true, LaunchRefusal.None, string.Empty);
    }

    /// <summary>Starts an app, if it is allowed to start.</summary>
    public LaunchResult Launch(KidAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var decision = CanLaunch(app);

        if (!decision.Allowed)
        {
            // Refused before anything is started, so the reason is the one the
            // child is shown rather than a launch failure.
            _logger.Info("Session", $"Launch of {app.Id} refused: {decision.Refusal}.");

            UpdateState(decision.Refusal switch
            {
                LaunchRefusal.ScreenTimeExpired => ChildSessionState.TimeUp,
                LaunchRefusal.OutsideAllowedHours => ChildSessionState.OutsideHours,
                _ => State
            });

            return decision.Refusal == LaunchRefusal.NotConfigured
                ? LaunchResult.NotConfigured(app)
                : LaunchResult.Blocked(app);
        }

        var result = _launcher.Launch(app);

        if (result.Status == LaunchStatus.Success)
        {
            ActiveApp = app;
            _appStartedAt = _time.GetUtcNow();
            UpdateState(ChildSessionState.InApp);

            // The app id and a duration. Nothing about what the child did
            // inside it.
            _logger.Info("Session", $"Child started {app.Id}.");
        }

        return result;
    }

    /// <summary>The child came back from an app.</summary>
    public void ReturnToHome()
    {
        if (ActiveApp is { } app && _appStartedAt is { } startedAt)
        {
            var duration = _time.GetUtcNow() - startedAt;
            _logger.Info("Session", $"Child left {app.Id} after {duration.TotalMinutes:F0} minutes.");
        }

        ActiveApp = null;
        _appStartedAt = null;

        // Re-evaluate: time may have run out while the child was in the app.
        var screenTime = _screenTime.Evaluate();

        UpdateState(screenTime.Status switch
        {
            ScreenTimeStatus.Expired => ChildSessionState.TimeUp,
            ScreenTimeStatus.OutsideAllowedHours => ChildSessionState.OutsideHours,
            _ => ChildSessionState.Home
        });
    }

    /// <summary>
    /// Re-checks screen time. Called on the same timer that ticks the engine,
    /// so an expiry reaches the child promptly rather than at the next launch.
    /// </summary>
    public void Refresh()
    {
        var screenTime = _screenTime.Evaluate();

        var next = screenTime.Status switch
        {
            ScreenTimeStatus.Expired => ChildSessionState.TimeUp,
            ScreenTimeStatus.OutsideAllowedHours => ChildSessionState.OutsideHours,
            _ => ActiveApp is null ? ChildSessionState.Home : ChildSessionState.InApp
        };

        UpdateState(next);
    }

    private void UpdateState(ChildSessionState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }
}
