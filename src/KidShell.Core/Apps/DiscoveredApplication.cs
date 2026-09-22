namespace KidShell.Core.Apps;

/// <summary>
/// How an application is started. This is not cosmetic: each kind has a
/// different launch mechanism, a different identity for future application
/// control, and different trust characteristics.
/// </summary>
public enum ApplicationKind
{
    Unknown = 0,

    /// <summary>A classic desktop program with an executable on disk.</summary>
    Win32 = 1,

    /// <summary>
    /// An MSIX/UWP package, launched by AUMID rather than by path. There is
    /// usually no executable a parent could sensibly point at.
    /// </summary>
    Packaged = 2,

    /// <summary>
    /// Started through a protocol or shell verb, e.g. <c>ms-settings:</c>.
    /// Carries no executable identity of its own.
    /// </summary>
    Protocol = 3,

    /// <summary>
    /// A launcher that starts something else - a game launcher, an updater
    /// stub. Recorded distinctly because allowing the launcher does not
    /// describe what it goes on to run.
    /// </summary>
    Launcher = 4
}

/// <summary>Where an entry was found. Used to merge duplicates sensibly.</summary>
public enum DiscoverySource
{
    Unknown = 0,
    StartMenu = 1,
    RegistryUninstall = 2,
    AppPaths = 3,
    PackagedApp = 4,
    KnownFolder = 5
}

/// <summary>
/// One application found on this machine.
///
/// Discovery is read-only and says nothing about whether an application is
/// allowed. Putting an entry in the child's grid is a separate, deliberate
/// act by a parent, and granting it future Windows application-control
/// permission is a third one. Conflating those would let anything installed on
/// the machine quietly become allowed.
/// </summary>
public sealed record DiscoveredApplication
{
    /// <summary>Stable identity for de-duplication. Lower-case.</summary>
    public required string Key { get; init; }

    /// <summary>Name as the machine reports it.</summary>
    public required string DisplayName { get; init; }

    public required ApplicationKind Kind { get; init; }

    public DiscoverySource Source { get; init; }

    /// <summary>Publisher where the machine states one. Never guessed.</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>Full executable path for <see cref="ApplicationKind.Win32"/>.</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>Arguments recorded by the shortcut, where any.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>
    /// Application User Model ID for packaged apps. The only reliable way to
    /// start one, and the identity application control would use.
    /// </summary>
    public string Aumid { get; init; } = string.Empty;

    /// <summary>Icon source on disk, where one was found.</summary>
    public string IconPath { get; init; } = string.Empty;

    /// <summary>Version string the machine reports, for diagnostics.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Whether the executable path was confirmed to exist at discovery time.
    /// A shortcut can outlive the program it points at.
    /// </summary>
    public bool TargetExists { get; init; }

    /// <summary>
    /// Whether KidShell recognises this as a program it has a profile for.
    /// Set during profile matching, not by the scanner.
    /// </summary>
    public string? ProfileId { get; init; }

    /// <summary>What a parent needs in order to launch this.</summary>
    public string LaunchTarget => Kind switch
    {
        ApplicationKind.Packaged => Aumid,
        _ => ExecutablePath
    };

    /// <summary>
    /// Whether KidShell could actually start this today. A packaged app needs
    /// an AUMID; a Win32 app needs an executable that exists.
    /// </summary>
    public bool IsLaunchable => Kind switch
    {
        ApplicationKind.Packaged => !string.IsNullOrWhiteSpace(Aumid),
        ApplicationKind.Protocol => !string.IsNullOrWhiteSpace(ExecutablePath),
        _ => !string.IsNullOrWhiteSpace(ExecutablePath) && TargetExists
    };
}
