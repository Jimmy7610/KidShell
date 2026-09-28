using KidShell.Core.Runtime;

namespace KidShell.Core.Launching;

/// <summary>
/// What may be approved, asked of the right rule for the kind of thing it is.
///
/// One entry point so that no caller has to remember which validator belongs
/// to which input. Feeding a packaged app's identity to the rule for
/// hand-typed executables is exactly what OPSV finding 03 was, and a single
/// front door is what stops it recurring.
/// </summary>
public static class LaunchTargetPolicy
{
    /// <summary>
    /// The protocol schemes KidShell is prepared to start, without the colon.
    ///
    /// Empty, deliberately. Nothing the product ships needs one: every default
    /// app is an executable, and a discovered Store app keeps its AUMID rather
    /// than a protocol. A scheme names whichever application happens to be
    /// registered for it, which can change after the parent approved it - so
    /// an open list would be an approval of something not yet decided.
    ///
    /// Adding one is a deliberate act with a test beside it, not a default.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProtocols = [];

    /// <summary>
    /// Whether a target may be approved for the given kind.
    /// </summary>
    public static ManualProgramCheck Check(ApplicationLaunchKind kind, string? target) => kind switch
    {
        ApplicationLaunchKind.Win32Executable => ManualProgramPolicy.Check(target),
        ApplicationLaunchKind.PackagedApp => CheckPackaged(target),
        ApplicationLaunchKind.UriProtocol => CheckProtocol(target),
        _ => new ManualProgramCheck(ManualProgramVerdict.Unsupported, "AddApp.PathUnsupported")
    };

    /// <summary>
    /// A packaged application's AUMID.
    ///
    /// Not required to end in .exe, which is the bug. An AUMID is
    /// <c>PackageFamilyName!ApplicationId</c> - it is not a path and does not
    /// have an extension, and demanding one refused every Store app on the
    /// machine.
    ///
    /// What IS required is that it looks like an AUMID rather than a path, so
    /// that a path cannot be smuggled in under a kind that skips the
    /// executable rules.
    /// </summary>
    private static ManualProgramCheck CheckPackaged(string? aumid)
    {
        var value = (aumid ?? string.Empty).Trim().Trim('"');

        if (value.Length == 0)
        {
            return new ManualProgramCheck(ManualProgramVerdict.Empty, "AddApp.PathRequired");
        }

        // A path wearing a packaged app's label would otherwise bypass the
        // script, shortcut and escape-surface rules entirely.
        if (WindowsPath.IsFullyQualified(value) || value.Contains('\\') || value.Contains('/'))
        {
            return new ManualProgramCheck(ManualProgramVerdict.Unsupported, "AddApp.PackagedNotAnIdentity");
        }

        // PackageFamilyName!ApplicationId. The separator is what makes it an
        // application rather than a package, and Windows needs the whole thing
        // to activate anything.
        var separator = value.IndexOf('!', StringComparison.Ordinal);

        if (separator <= 0 || separator == value.Length - 1)
        {
            return new ManualProgramCheck(ManualProgramVerdict.Unsupported, "AddApp.PackagedNotAnIdentity");
        }

        return new ManualProgramCheck(ManualProgramVerdict.Ok, string.Empty);
    }

    /// <summary>
    /// A protocol activation, against the explicit list.
    /// </summary>
    private static ManualProgramCheck CheckProtocol(string? target)
    {
        var value = (target ?? string.Empty).Trim().Trim('"');

        if (value.Length == 0)
        {
            return new ManualProgramCheck(ManualProgramVerdict.Empty, "AddApp.PathRequired");
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);

        if (separator <= 0)
        {
            return new ManualProgramCheck(ManualProgramVerdict.Unsupported, "AddApp.ProtocolUnsupported");
        }

        var scheme = value[..separator];

        return SupportedProtocols.Contains(scheme, StringComparer.OrdinalIgnoreCase)
            ? new ManualProgramCheck(ManualProgramVerdict.Ok, string.Empty)
            : new ManualProgramCheck(ManualProgramVerdict.Unsupported, "AddApp.ProtocolUnsupported");
    }
}
