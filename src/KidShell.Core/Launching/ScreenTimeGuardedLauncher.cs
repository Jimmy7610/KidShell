using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.ScreenTime;

namespace KidShell.Core.Launching;

/// <summary>
/// The screen-time gate, placed where nothing can go round it.
///
/// WHY THIS IS NOT JUST A CHECK IN THE VIEW MODEL
/// ----------------------------------------------
/// Both callers that start an app already ask whether screen time allows it,
/// and both are correct. But that is a convention - it holds because two
/// authors remembered, and it holds only until somebody adds a third caller.
/// The external audit found exactly that class of bug: a launch path that
/// skipped the check. Fixing the path fixes one bug; moving the gate to the
/// single point every launch passes through fixes the shape of the bug.
///
/// So this decorates the real launcher, and dependency injection hands this
/// out as <see cref="IAppLauncher"/>. A future caller cannot start an app
/// without going through it, because there is no other way to start an app.
///
/// The callers keep their own checks. They are not redundant: they are what
/// lets the child be shown "tiden är slut" with an explanation, rather than a
/// launch that silently does nothing. This is the backstop, not the message.
/// </summary>
public sealed class ScreenTimeGuardedLauncher : IAppLauncher
{
    private readonly IAppLauncher _inner;
    private readonly IScreenTimeCoordinator _screenTime;
    private readonly IKidShellLogger _logger;

    public ScreenTimeGuardedLauncher(
        IAppLauncher inner,
        IScreenTimeCoordinator screenTime,
        IKidShellLogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(screenTime);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _screenTime = screenTime;
        _logger = logger;
    }

    public LaunchResult Launch(KidAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Asked here, at the moment of launching, rather than trusted from
        // whenever the screen was drawn. The allowance can run out while a
        // tile is on screen, and a tap already in flight must not get through
        // on the strength of a check made a minute ago.
        if (!_screenTime.CanLaunch())
        {
            _logger.Info("Launch", $"Refused to start {app.Id}: screen time does not allow it.");
            return LaunchResult.Blocked(app);
        }

        return _inner.Launch(app);
    }
}
