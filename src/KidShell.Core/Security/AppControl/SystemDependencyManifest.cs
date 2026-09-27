namespace KidShell.Core.Security.AppControl;

/// <summary>One Windows component the child's session cannot do without.</summary>
/// <param name="FileName">The executable, as it is named under System32.</param>
/// <param name="Reason">Why a child session breaks without it. Reviewed per entry.</param>
public sealed record SystemDependency(string FileName, string Reason);

/// <summary>
/// The Windows components a locked-down child session genuinely needs, named
/// one at a time.
///
/// WHY NOT %WINDIR%\*
/// ------------------
/// Because Microsoft says not to. Their own guidance calls the default rules
/// "a starter policy when you are first testing AppLocker" and then says, of
/// the very rule KidShell used to emit:
///
///     "the default rule to allow all users to run .exe files in the Windows
///      folder is based on a path condition that allows all files within the
///      Windows folder to run. The Windows folder contains a Temp subfolder to
///      which the Users group is given [...] Create Files/Write Data"
///
/// So %WINDIR%\* allows anything the child can drop into %WINDIR%\Temp, which
/// they can write to by design. The same guidance on path rules adds that a
/// folder rule "might be less secure if [it] contains subfolders that are
/// writable by nonadministrators". A parent-facing allowlist built on that is
/// not an allowlist.
///
/// %PROGRAMFILES%\* was worse in a different way: it is not a Windows
/// requirement at all. It allowed every installed program on the machine,
/// which is precisely the set of things the parent was choosing between.
///
/// WHAT IS ON THE LIST, AND WHAT IS NOT
/// ------------------------------------
/// Only processes that run AS THE CHILD. AppLocker evaluates rules against the
/// identity running the process, and this policy is scoped to the child's SID,
/// so services running as SYSTEM or LocalService are unaffected and do not
/// need entries. That removes most of Windows from the question at a stroke.
///
/// Every entry carries the reason it is here. An entry nobody can justify does
/// not belong, and the test suite asserts that each one has a reason written
/// against it.
///
/// See <see cref="EscapeSurfaces"/> for the executables that are deliberately
/// absent, which is the more interesting list.
/// </summary>
public static class SystemDependencyManifest
{
    /// <summary>
    /// The AppLocker path variable for System32. AppLocker interprets its own
    /// variables rather than environment ones, and this is the documented
    /// spelling; it also covers SysWOW64 on 64-bit Windows.
    /// </summary>
    public const string System32 = "%SYSTEM32%";

    /// <summary>
    /// Processes the signed-in child runs during an ordinary session.
    /// </summary>
    public static readonly IReadOnlyList<SystemDependency> Required =
    [
        new("userinit.exe",
            "Windows runs this as the user at sign-in; it starts the shell. Without it the session never reaches a desktop."),

        new("explorer.exe",
            "The shell that hosts the desktop and the taskbar in Standard Mode. Assigned Access replaces it, and Secure Mode does not need this entry."),

        new("sihost.exe",
            "Shell Infrastructure Host. Runs as the user and provides the parts of the shell that action centre and toasts depend on."),

        new("taskhostw.exe",
            "Runs per-user scheduled and background tasks that Windows itself relies on during a session."),

        new("ctfmon.exe",
            "Text input. Without it the on-screen and touch keyboards do not work, which on a tablet means the child cannot type at all."),

        new("RuntimeBroker.exe",
            "Brokers capability checks for packaged apps. Any Store app the parent approved fails to start without it."),

        new("ApplicationFrameHost.exe",
            "Hosts the window frame of packaged apps. Without it an approved Store app has no window."),

        new("dllhost.exe",
            "COM surrogate. Windows starts it as the user for thumbnailing and other in-process work the shell depends on."),

        new("conhost.exe",
            "Console host. Windows starts it for any console process; a program the parent approved that writes to a console fails without it.")
    ];

    /// <summary>
    /// The full AppLocker path for a dependency.
    /// </summary>
    public static string PathFor(SystemDependency dependency) =>
        $@"{System32}\{dependency.FileName}";
}
