namespace KidShell.Core.Security.AppControl;

/// <summary>One program that must never end up in an allow rule, and why.</summary>
public sealed record EscapeSurface(string FileName, string Reason);

/// <summary>
/// Programs that would hand a child a way out, listed so that a test can prove
/// they never appear in a generated policy.
///
/// WHY A LIST OF THINGS WE DO NOT ALLOW
/// ------------------------------------
/// A default-deny policy does not need block rules: anything absent is already
/// refused. This list is not how they are blocked - it is how we CHECK they
/// are blocked. Every one of these used to be allowed, because %WINDIR%\*
/// allowed all of them, and nothing in the build would have noticed.
///
/// So this is the regression test's list, not the policy's. If a future change
/// widens a rule far enough to readmit any of these, the suite fails and names
/// the one it readmitted.
///
/// Each entry is an interpreter, an administrative tool, or a well-known
/// living-off-the-land binary: something that can run code the parent never
/// approved, change settings the parent set, or fetch a program from the
/// internet. It is not exhaustive - no such list is - and the policy is not
/// built from it. The policy is built from
/// <see cref="SystemDependencyManifest"/>, which names only what is needed.
/// This list is how we notice if that stops being true.
/// </summary>
public static class EscapeSurfaces
{
    public static readonly IReadOnlyList<EscapeSurface> Known =
    [
        // ---------------------------------------------------- interpreters
        new("cmd.exe", "A command prompt runs anything, including copies of programs the parent removed."),
        new("powershell.exe", "Runs arbitrary code, downloads files, and changes settings."),
        new("pwsh.exe", "The same, from a separate installation the policy would not otherwise cover."),
        new("wscript.exe", "Runs VBScript and JScript files, which a child can author in Notepad."),
        new("cscript.exe", "The console host for the same scripting engines."),
        new("mshta.exe", "Runs HTML applications, which are scripts wearing a window."),
        new("wsl.exe", "A whole second operating system, unaffected by Windows application control."),
        new("bash.exe", "A shell. Everything a command prompt gives, plus a package manager."),

        // ---------------------------------------- administration and settings
        new("regedit.exe", "Edits the registry, including the keys KidShell's own restrictions live in."),
        new("reg.exe", "The same from a command line, and scriptable."),
        new("mmc.exe", "Hosts the management consoles, including Local Users and Groups."),
        new("control.exe", "Opens Control Panel applets, several of which change security settings."),
        new("msconfig.exe", "Changes what starts with Windows."),
        new("taskmgr.exe", "Ends KidShell, and starts anything by path from Run new task."),
        new("schtasks.exe", "Schedules a program to run later, outside anything KidShell watches."),
        new("at.exe", "The older scheduler, same effect."),
        new("wmic.exe", "Starts processes and queries the machine; a classic bypass."),
        new("net.exe", "Changes group membership, given the rights to do so."),
        new("net1.exe", "The same binary under its other name, which is how net.exe blocks get missed."),

        // -------------------------------------------- living off the land
        new("rundll32.exe", "Runs code from a DLL, which is how a blocked executable gets run anyway."),
        new("regsvr32.exe", "Registers - and thereby executes - a DLL, including a remote one."),
        new("certutil.exe", "Downloads and decodes files. A download tool that does not look like one."),
        new("bitsadmin.exe", "Downloads files in the background."),
        new("installutil.exe", "Executes .NET assemblies through an installer entry point."),
        new("msbuild.exe", "Executes arbitrary tasks from a project file."),
        new("forfiles.exe", "Runs a command for each file found, which is a command runner."),
        new("pcalua.exe", "The compatibility assistant launcher, which launches things."),
        new("ftp.exe", "Transfers files, and runs local commands from a script.")

        // NOT explorer.exe, deliberately.
        //
        // It is genuinely both: a file browser a child can launch things from,
        // and the shell that draws the desktop. In Standard Mode it has to run
        // or there is no session at all, so it is on the required manifest and
        // its risk is handled by the mode rather than by a rule - Secure Mode
        // uses Assigned Access, which replaces the shell entirely and does not
        // need this entry. Listing it in both places would make the two lists
        // contradict each other, and a contradiction resolves to whichever
        // check runs last, which is not a security decision.
    ];

    /// <summary>
    /// Whether a path or rule value would admit one of these.
    ///
    /// Deliberately blunt: it matches on the file name anywhere in the value,
    /// so a rule for a folder that happens to contain cmd.exe is caught as
    /// well as a rule naming it directly.
    /// </summary>
    public static EscapeSurface? Matching(string ruleValue)
    {
        var value = (ruleValue ?? string.Empty).Replace('/', '\\').ToLowerInvariant();

        foreach (var surface in Known)
        {
            var name = surface.FileName.Split(' ')[0].ToLowerInvariant();

            if (value.EndsWith('\\' + name, StringComparison.Ordinal) ||
                string.Equals(value, name, StringComparison.Ordinal))
            {
                return surface;
            }
        }

        return null;
    }
}
