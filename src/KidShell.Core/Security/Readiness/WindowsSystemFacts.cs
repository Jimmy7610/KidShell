namespace KidShell.Core.Security.Readiness;

/// <summary>
/// Raw, unprocessed facts read from the machine.
///
/// This is the only type that carries platform data across the boundary, and
/// it holds facts rather than conclusions: no "supports X" booleans, because
/// deciding those is <see cref="WindowsCapabilityAnalyzer"/>'s job and has to
/// be testable without a Windows machine.
/// </summary>
public sealed record WindowsSystemFacts
{
    /// <summary>Registry EditionID, e.g. "Core", "Professional".</summary>
    public string EditionId { get; init; } = string.Empty;

    /// <summary>
    /// Registry ProductName. Captured for diagnostics only. It reads
    /// "Windows 10 ..." even on Windows 11, so nothing decides anything on it.
    /// </summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>Feature-update label, e.g. "25H2".</summary>
    public string DisplayVersion { get; init; } = string.Empty;

    /// <summary>OS build, e.g. 26200.</summary>
    public int BuildNumber { get; init; }

    /// <summary>Update build revision, the part after the build.</summary>
    public int UpdateBuildRevision { get; init; }

    /// <summary>HKLM EnableLUA. Null when the value could not be read.</summary>
    public bool? IsUacEnabled { get; init; }

    /// <summary>
    /// Whether the process token itself shows Administrators membership.
    ///
    /// This is NOT the same as "the account is an administrator". Under UAC an
    /// administrator runs on a filtered token, and the managed token API drops
    /// the deny-only Administrators SID altogether, so this reads false for a
    /// perfectly ordinary admin. The account question is answered by
    /// cross-referencing the local account list instead - see
    /// WindowsCapabilityAnalyzer.
    /// </summary>
    public bool TokenShowsAdministrator { get; init; }

    /// <summary>
    /// Whether THIS PROCESS is currently elevated. Separate from
    /// <see cref="CurrentUserIsAdministrator"/>: secure setup needs an
    /// administrator account, and will additionally need elevation at the
    /// moment it runs.
    /// </summary>
    public bool IsProcessElevated { get; init; }

    /// <summary>Signed-in account name.</summary>
    public string CurrentUserName { get; init; } = string.Empty;

    /// <summary>SID of the signed-in account, used to match it in the account list.</summary>
    public string CurrentUserSid { get; init; } = string.Empty;

    /// <summary>Whether the process is running with a package identity (MSIX).</summary>
    public bool HasPackageIdentity { get; init; }

    // ---------------- AppLocker management surface, probed read-only ------
    // These are properties of THIS MACHINE, not of the edition. Whether
    // AppLocker can be enforced is a version question; whether a policy can
    // be installed depends on what is actually present here.

    /// <summary>The AppLocker PowerShell module and its cmdlets are available.</summary>
    public bool AppLockerModuleAvailable { get; init; }

    /// <summary>Get-AppLockerPolicy -Local could be read without error.</summary>
    public bool AppLockerLocalPolicyReadable { get; init; }

    /// <summary>Application Identity (AppIDSvc), the rule evaluation service, exists.</summary>
    public bool AppIdentityServicePresent { get; init; }

    /// <summary>AppIDSvc start mode, for diagnostics only. Never changed.</summary>
    public string AppIdentityServiceStartMode { get; init; } = string.Empty;

    /// <summary>The %windir%\System32\AppLocker policy store directory exists.</summary>
    public bool AppLockerPolicyStorePresent { get; init; }

    /// <summary>secpol.msc or gpedit.msc is present. Absent on Home.</summary>
    public bool LocalSecurityPolicyUiPresent { get; init; }

    /// <summary>True when detection itself failed; the reason is in <see cref="DetectionError"/>.</summary>
    public bool DetectionFailed { get; init; }

    public string? DetectionError { get; init; }

    /// <summary>
    /// Facts for a machine nothing could be read from. Everything downstream
    /// then resolves to the least capable answer rather than a guess.
    /// </summary>
    public static WindowsSystemFacts Unknown(string? error = null) => new()
    {
        DetectionFailed = true,
        DetectionError = error
    };
}

/// <summary>
/// Reads <see cref="WindowsSystemFacts"/> from the machine. Read-only by
/// contract: implementations may not change any Windows setting.
/// </summary>
public interface ISystemFactsProvider
{
    Task<WindowsSystemFacts> ReadAsync(CancellationToken cancellationToken = default);
}
