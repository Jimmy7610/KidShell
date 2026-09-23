using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using Microsoft.UI.Windowing;

namespace KidShell.App.Services;

/// <summary>
/// Applies the presentation rule to a window.
///
/// The rule itself lives in KidShell.Core.Runtime.ShellPresentation, where it
/// can be tested without a window. This class is the part that touches WinUI.
///
/// PRESENTATION IS NOT SECURITY
/// ----------------------------
/// Full screen stops a child noticing the rest of Windows. It does not stop
/// them reaching it: Alt+Tab, the Windows key, Ctrl+Alt+Delete and Task Manager
/// all still work, and the escape matrix says so in every row.
///
/// That distinction is why this lives in its own small class rather than being
/// folded into the security code. Making a window borderless is a presentation
/// choice; containing a child is something only Windows can do, through
/// Assigned Access. Treating the first as the second is how a parent ends up
/// believing a machine is locked when it is not.
///
/// A DEVELOPER IS NEVER TRAPPED
/// ----------------------------
/// A Debug build stays windowed no matter what, because a full-screen window
/// with no title bar, on the machine somebody is writing the code on, is a
/// bad afternoon. The decision is made from the compile-time runtime mode, so
/// there is no setting a child could find that turns it back on either.
/// </summary>
public interface IChildPresentation
{
    /// <summary>How the window should be presented for the given mode.</summary>
    WindowPresentation Decide(bool isChildMode);

    /// <summary>Applies the decision to a window.</summary>
    void Apply(AppWindow window, bool isChildMode);
}

public sealed class ChildPresentation : IChildPresentation
{
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;

    private WindowPresentation? _applied;

    public ChildPresentation(IRuntimeEnvironment environment, IKidShellLogger logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public WindowPresentation Decide(bool isChildMode) =>
        ShellPresentation.Decide(_environment, isChildMode);

    public void Apply(AppWindow window, bool isChildMode)
    {
        ArgumentNullException.ThrowIfNull(window);

        var target = Decide(isChildMode);

        // Re-applying the same presenter resets focus and makes the window
        // flash, which a child would notice every time Parent Mode closes.
        if (_applied == target)
        {
            return;
        }

        try
        {
            switch (target)
            {
                case WindowPresentation.FullScreen:
                    window.SetPresenter(AppWindowPresenterKind.FullScreen);
                    break;

                default:
                    window.SetPresenter(AppWindowPresenterKind.Overlapped);
                    break;
            }

            _applied = target;
            _logger.Info("Shell", $"Window presentation set to {target}.");
        }
        catch (Exception ex)
        {
            // A presenter that will not apply must not take the app down. The
            // window stays as it was, which is always a usable state.
            _logger.Warning("Shell", $"Could not set window presentation to {target}.", ex);
        }
    }
}
