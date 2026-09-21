namespace KidShell.Core.Security.Readiness;

/// <summary>Whether a given platform capability can be used on this machine.</summary>
public enum CapabilityState
{
    /// <summary>Could not be determined. Treated as unavailable.</summary>
    Unknown = 0,

    /// <summary>The edition supports it and KidShell could use it later.</summary>
    Available = 1,

    /// <summary>The edition does not support it.</summary>
    Unavailable = 2
}

/// <summary>
/// What this machine could support, derived from
/// <see cref="WindowsSystemFacts"/> by <see cref="WindowsCapabilityAnalyzer"/>.
///
/// Nothing here says anything is switched on. Every field answers "could
/// KidShell use this in a later milestone", never "is the child protected".
/// </summary>
public sealed record WindowsSecurityCapabilities
{
    public required string EditionDisplayName { get; init; }

    public required WindowsEdition Edition { get; init; }

    public required WindowsGeneration Generation { get; init; }

    /// <summary>Feature-update label, e.g. "25H2".</summary>
    public string Version { get; init; } = string.Empty;

    public int BuildNumber { get; init; }

    public int UpdateBuildRevision { get; init; }

    /// <summary>Assigned Access / restricted user experience. Pro and above.</summary>
    public CapabilityState AssignedAccess { get; init; }

    /// <summary>
    /// OS-level AppLocker enforcement. Enterprise and Education only —
    /// deliberately narrower than <see cref="AssignedAccess"/>, since the two
    /// are separate features with separate edition requirements.
    /// </summary>
    public CapabilityState AppLocker { get; init; }

    /// <summary>
    /// KidShell's own launcher allowlist, which gates what the child grid can
    /// start. Works on every edition and is the app-control story for Standard
    /// mode. It is not an OS guarantee and is never presented as one.
    /// </summary>
    public CapabilityState KidShellAppAllowlist { get; init; }

    public bool SupportsAssignedAccess => AssignedAccess == CapabilityState.Available;

    public bool SupportsAppLocker => AppLocker == CapabilityState.Available;

    /// <summary>
    /// Whether the full Secure path is possible here. Requires Assigned Access
    /// and UAC: without UAC the separate child account provides no real
    /// separation, so Secure would be a claim KidShell could not back up.
    /// </summary>
    public bool SupportsSecureMode { get; init; }

    public bool? IsUacEnabled { get; init; }

    /// <summary>The account is an administrator, elevated or not.</summary>
    public bool CurrentUserIsAdministrator { get; init; }

    /// <summary>This process is running elevated right now.</summary>
    public bool IsProcessElevated { get; init; }

    public string CurrentUserName { get; init; } = string.Empty;

    public bool HasPackageIdentity { get; init; }

    /// <summary>The best mode this machine could reach, not the mode it is in.</summary>
    public required SecurityMode RecommendedSecurityMode { get; init; }

    public bool DetectionFailed { get; init; }

    public string? DetectionError { get; init; }

    /// <summary>Non-fatal findings, in Swedish, safe to show a parent.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Findings that prevent the recommended mode, in Swedish.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
}
