namespace KidShell.Core.Runtime;

/// <summary>How KidShell's window should be presented.</summary>
public enum WindowPresentation
{
    /// <summary>
    /// An ordinary resizable window with a title bar. What a developer needs,
    /// and what first-run setup and Parent Mode use on any build.
    /// </summary>
    Windowed = 0,

    /// <summary>Borderless full screen. What a child sees in a shipped build.</summary>
    FullScreen = 1
}

/// <summary>
/// Decides how the window is presented.
///
/// PRESENTATION IS NOT SECURITY
/// ----------------------------
/// Full screen stops a child noticing the rest of Windows. It does not stop
/// them reaching it: Alt+Tab, the Windows key, Ctrl+Alt+Delete and Task Manager
/// all still work, and every row of the escape matrix says so.
///
/// The rule lives here, apart from the code that applies it, for that reason.
/// Making a window borderless is a presentation choice; containing a child is
/// something only Windows can do, through Assigned Access. Treating the first
/// as the second is how a parent ends up believing a machine is locked when it
/// is not.
/// </summary>
public static class ShellPresentation
{
    /// <summary>
    /// What presentation to use.
    ///
    /// A developer build is always windowed, whatever the mode: a borderless
    /// full-screen window with no title bar, on the machine somebody is writing
    /// the code on, is a bad afternoon. The decision comes from the
    /// compile-time runtime mode, so there is no setting a child could find
    /// that turns it back on either.
    ///
    /// Parent Mode and first-run setup stay windowed even in a shipped build -
    /// an adult needs the ordinary window controls while they work.
    /// </summary>
    public static WindowPresentation Decide(IRuntimeEnvironment environment, bool isChildMode)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment)
        {
            return WindowPresentation.Windowed;
        }

        return isChildMode ? WindowPresentation.FullScreen : WindowPresentation.Windowed;
    }
}
