using KidShell.Core.Security.AppControl;

namespace KidShell.Core.Launching;

/// <summary>Why a hand-picked program was refused.</summary>
public enum ManualProgramVerdict
{
    Ok = 0,

    /// <summary>Nothing was chosen.</summary>
    Empty = 1,

    /// <summary>A script. Running it means running an interpreter.</summary>
    Script = 2,

    /// <summary>A shortcut. What it points at is not what was chosen.</summary>
    Shortcut = 3,

    /// <summary>Something KidShell has no safe way to start.</summary>
    Unsupported = 4,

    /// <summary>An interpreter or administrative tool, chosen directly.</summary>
    EscapeSurface = 5
}

public sealed record ManualProgramCheck(ManualProgramVerdict Verdict, string ResourceKey)
{
    public bool IsAllowed => Verdict == ManualProgramVerdict.Ok;
}

/// <summary>
/// What a parent is allowed to pick by hand when adding a program.
///
/// EXTERNAL AUDIT FINDING 08C. The file picker offered .exe, .lnk, .bat and
/// .cmd. Only the first of those is something KidShell can start safely, and
/// the other three are worse than merely unsupported.
///
///  * .bat and .cmd are not programs. Running one runs cmd.exe, which is an
///    interpreter that will run anything at all - and which KidShell's own
///    AppLocker policy refuses by name. A parent adding a batch file would
///    have got a tile that Secure Mode then blocked, having been told it was
///    an approved app.
///
///  * .lnk is a shortcut, so what the parent chose and what would actually
///    run are two different files. The target can be changed afterwards
///    without touching the shortcut the parent approved, which makes the
///    approval meaningless. Supporting it properly means resolving the target
///    and approving THAT, which is a feature rather than a filter, and is not
///    in this pass.
///
/// The rule is therefore: a program KidShell can start directly, by path.
/// Packaged applications are not picked as files at all - they come from the
/// installed-app catalogue with their AUMID, which is the only thing that
/// identifies them.
///
/// This lives in Core rather than in the picker's filter because a filter is
/// a suggestion. A parent can type a path, and a file dialog's type list can
/// be defeated by typing *.* into the name box.
/// </summary>
public static class ManualProgramPolicy
{
    /// <summary>The only extension a hand-picked program may have.</summary>
    public const string SupportedExtension = ".exe";

    /// <summary>Extensions the picker offers. One, deliberately.</summary>
    public static readonly IReadOnlyList<string> PickerFilter = [SupportedExtension];

    private static readonly string[] ScriptExtensions =
        [".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".vbe", ".wsf", ".wsh", ".hta", ".py", ".pl"];

    public static ManualProgramCheck Check(string? path)
    {
        var value = (path ?? string.Empty).Trim().Trim('"');

        if (value.Length == 0)
        {
            return new ManualProgramCheck(ManualProgramVerdict.Empty, "AddApp.PathRequired");
        }

        var name = WindowsPath.FileName(value);
        var extension = ExtensionOf(name);

        if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return new ManualProgramCheck(ManualProgramVerdict.Script, "AddApp.PathScript");
        }

        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return new ManualProgramCheck(ManualProgramVerdict.Shortcut, "AddApp.PathShortcut");
        }

        if (!string.Equals(extension, SupportedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new ManualProgramCheck(ManualProgramVerdict.Unsupported, "AddApp.PathUnsupported");
        }

        // A parent can browse to cmd.exe as easily as to mspaint.exe, and it
        // is an .exe, so the extension rule alone would let it through.
        if (EscapeSurfaces.Matching(name) is not null)
        {
            return new ManualProgramCheck(ManualProgramVerdict.EscapeSurface, "AddApp.PathNotSafe");
        }

        return new ManualProgramCheck(ManualProgramVerdict.Ok, string.Empty);
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[dot..];
    }
}
