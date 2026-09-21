namespace KidShell.Core.Security.Readiness;

/// <summary>
/// Turns raw machine facts into capability answers.
///
/// Pure and static on purpose: every edition/capability decision in KidShell
/// goes through here, so the rules are in one place, and the whole matrix can
/// be tested without a Windows machine of that edition.
/// </summary>
public static class WindowsCapabilityAnalyzer
{
    /// <summary>
    /// Editions with Assigned Access (single-app kiosk and the restricted user
    /// experience). Home is absent, which is the entire reason Standard mode
    /// exists.
    /// </summary>
    private static readonly HashSet<WindowsEdition> AssignedAccessEditions =
    [
        WindowsEdition.Pro,
        WindowsEdition.ProEducation,
        WindowsEdition.ProForWorkstations,
        WindowsEdition.Enterprise,
        WindowsEdition.Education,
        WindowsEdition.IoTEnterprise
    ];

    /// <summary>
    /// Editions where the AppLocker CSP - the MDM management channel - is
    /// documented as available.
    ///
    /// This is a DEPLOYMENT channel, not an enforcement requirement. Since
    /// KB 5024351, enforcement itself needs no particular edition; only the
    /// ways of installing a policy still differ. Home is absent here.
    /// </summary>
    private static readonly HashSet<WindowsEdition> AppLockerCspEditions =
    [
        WindowsEdition.Pro,
        WindowsEdition.ProEducation,
        WindowsEdition.ProForWorkstations,
        WindowsEdition.Enterprise,
        WindowsEdition.Education,
        WindowsEdition.IoTEnterprise
    ];

    /// <summary>
    /// First Windows 10 build that enforces AppLocker on every edition.
    /// Version 2004 (build 19041) with KB 5024351.
    /// </summary>
    private const int AppLockerAllEditionsMinimumWindows10Build = 19041;

    /// <summary>
    /// Maps facts to capabilities.
    ///
    /// <paramref name="accounts"/> is optional and used only to answer
    /// "is the signed-in account an administrator" for an unelevated process,
    /// where the token cannot say.
    /// </summary>
    public static WindowsSecurityCapabilities Analyze(
        WindowsSystemFacts facts,
        IReadOnlyList<WindowsAccount>? accounts = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var currentUserIsAdministrator = ResolveAdministrator(facts, accounts);

        var edition = WindowsEditionMap.FromEditionId(facts.EditionId);
        var generation = WindowsEditionMap.FromBuild(facts.BuildNumber);

        var warnings = new List<string>();
        var blockers = new List<string>();

        // An unreadable machine is treated as the least capable one. Guessing
        // upwards here would mean promising a parent a protection that the
        // next milestone then could not deliver.
        var unknownPlatform = facts.DetectionFailed || edition == WindowsEdition.Unknown;

        var assignedAccess = unknownPlatform
            ? CapabilityState.Unknown
            : AssignedAccessEditions.Contains(edition) ? CapabilityState.Available : CapabilityState.Unavailable;

        var appControl = AnalyzeAppControl(facts, edition, generation, unknownPlatform);

        // KidShell's own allowlist is part of the app, so it works anywhere
        // KidShell runs at all.
        var ownAllowlist = CapabilityState.Available;

        if (facts.DetectionFailed)
        {
            warnings.Add("Windows-informationen kunde inte läsas. KidShell utgår från de mest begränsade inställningarna.");
        }
        else if (edition == WindowsEdition.Unknown)
        {
            warnings.Add("Windows-utgåvan kunde inte identifieras. KidShell utgår från de mest begränsade inställningarna.");
        }

        if (generation == WindowsGeneration.Legacy)
        {
            blockers.Add("KidShell kräver Windows 10 eller senare.");
        }

        var uacEnabled = facts.IsUacEnabled;

        if (uacEnabled is false)
        {
            // Without UAC a standard child account is not meaningfully
            // separated from the administrator, so Secure cannot be honest.
            blockers.Add("Windows-kontroll av användarkonton (UAC) är avstängd. Den behövs för att skilja barnets konto från ditt.");
        }
        else if (uacEnabled is null && !facts.DetectionFailed)
        {
            warnings.Add("Status för användarkontokontroll (UAC) kunde inte läsas.");
        }

        var supportsSecure =
            assignedAccess == CapabilityState.Available &&
            uacEnabled is true &&
            generation is WindowsGeneration.Windows10 or WindowsGeneration.Windows11;

        if (assignedAccess == CapabilityState.Unavailable)
        {
            warnings.Add(
                $"Secure Mode kräver en Windows-utgåva som stöder Assigned Access. " +
                $"{WindowsEditionMap.DisplayName(generation, edition)} gör inte det.");
        }

        if (!currentUserIsAdministrator)
        {
            warnings.Add("Du är inloggad som standardanvändare. Säkerhetsinstallation kräver ett administratörskonto.");
        }

        var recommended = DetermineRecommendedMode(supportsSecure, generation, blockers.Count > 0);

        return new WindowsSecurityCapabilities
        {
            Edition = edition,
            Generation = generation,
            EditionDisplayName = WindowsEditionMap.DisplayName(generation, edition),
            Version = facts.DisplayVersion,
            BuildNumber = facts.BuildNumber,
            UpdateBuildRevision = facts.UpdateBuildRevision,

            AssignedAccess = assignedAccess,
            AppControl = appControl,
            KidShellAppAllowlist = ownAllowlist,
            SupportsSecureMode = supportsSecure,

            IsUacEnabled = uacEnabled,
            CurrentUserIsAdministrator = currentUserIsAdministrator,
            IsProcessElevated = facts.IsProcessElevated,
            CurrentUserName = facts.CurrentUserName,
            HasPackageIdentity = facts.HasPackageIdentity,

            RecommendedSecurityMode = recommended,

            DetectionFailed = facts.DetectionFailed,
            DetectionError = facts.DetectionError,

            Warnings = warnings,
            Blockers = blockers
        };
    }

    /// <summary>
    /// Works out the AppLocker picture, keeping enforcement and deployment
    /// apart.
    ///
    /// Enforcement follows Microsoft's requirements table: Windows 10 version
    /// 2004 and newer and all Windows 11 versions enforce AppLocker policies
    /// on every edition (KB 5024351). It is therefore decided by the build
    /// number, NOT by the edition - the previous edition-gated version of this
    /// code was simply wrong, and told Home users a capability they have was
    /// missing.
    ///
    /// Deployment is the part that still varies. The CSP has edition
    /// requirements; the PowerShell module and the policy console are either
    /// installed on a given machine or they are not, so those are probed
    /// rather than inferred.
    /// </summary>
    private static AppControlCapabilities AnalyzeAppControl(
        WindowsSystemFacts facts,
        WindowsEdition edition,
        WindowsGeneration generation,
        bool unknownPlatform)
    {
        if (facts.DetectionFailed)
        {
            return AppControlCapabilities.Unknown;
        }

        var enforcement = generation switch
        {
            WindowsGeneration.Windows11 => CapabilityState.Available,
            WindowsGeneration.Windows10 when facts.BuildNumber >= AppLockerAllEditionsMinimumWindows10Build
                => CapabilityState.Available,

            // Older Windows 10 enforced AppLocker only on Enterprise and
            // Education, and only through Group Policy.
            WindowsGeneration.Windows10 => LegacyEnforcement(edition),

            _ => CapabilityState.Unknown
        };

        var csp = unknownPlatform
            ? CapabilityState.Unknown
            : AppLockerCspEditions.Contains(edition) ? CapabilityState.Available : CapabilityState.Unavailable;

        return new AppControlCapabilities
        {
            Enforcement = enforcement,
            EnforcementService = State(facts.AppIdentityServicePresent),
            EnforcementServiceStartMode = facts.AppIdentityServiceStartMode,
            PowerShellManagement = State(facts.AppLockerModuleAvailable),
            LocalPolicyReadable = State(facts.AppLockerLocalPolicyReadable),
            LocalPolicyStore = State(facts.AppLockerPolicyStorePresent),
            ManagementUi = State(facts.LocalSecurityPolicyUiPresent),
            Csp = csp
        };

        static CapabilityState LegacyEnforcement(WindowsEdition edition) =>
            edition is WindowsEdition.Enterprise or WindowsEdition.Education or WindowsEdition.IoTEnterprise
                ? CapabilityState.Available
                : CapabilityState.Unavailable;

        static CapabilityState State(bool present) =>
            present ? CapabilityState.Available : CapabilityState.Unavailable;
    }

    /// <summary>
    /// Whether the signed-in account is an administrator.
    ///
    /// An elevated token settles it outright. Otherwise the account is matched
    /// against the enumerated local accounts by SID (falling back to name),
    /// because a UAC-filtered token simply does not carry the Administrators
    /// SID and would misreport every unelevated administrator as a standard
    /// user - inventing a blocker that is not real.
    /// </summary>
    private static bool ResolveAdministrator(WindowsSystemFacts facts, IReadOnlyList<WindowsAccount>? accounts)
    {
        if (facts.IsProcessElevated || facts.TokenShowsAdministrator)
        {
            return true;
        }

        if (accounts is null || accounts.Count == 0)
        {
            return false;
        }

        // When a SID is known it is the only thing trusted: falling back to
        // the display name after a SID mismatch could match a different
        // account that merely shares a name, and grant administrator wrongly.
        var match = string.IsNullOrEmpty(facts.CurrentUserSid)
            ? accounts.FirstOrDefault(a => string.Equals(a.Username, facts.CurrentUserName, StringComparison.OrdinalIgnoreCase))
            : accounts.FirstOrDefault(a => string.Equals(a.Sid, facts.CurrentUserSid, StringComparison.OrdinalIgnoreCase));

        return match?.IsAdministrator ?? false;
    }

    /// <summary>
    /// The best mode the machine could reach. This is a property of the
    /// hardware and edition, never of what KidShell has actually applied.
    /// </summary>
    private static SecurityMode DetermineRecommendedMode(
        bool supportsSecure,
        WindowsGeneration generation,
        bool hasBlockers)
    {
        if (generation is WindowsGeneration.Legacy or WindowsGeneration.Unknown)
        {
            return SecurityMode.Development;
        }

        if (supportsSecure && !hasBlockers)
        {
            return SecurityMode.Secure;
        }

        // Standard still needs a working UAC-separated account, so a blocker
        // drops the recommendation all the way back.
        return hasBlockers ? SecurityMode.Development : SecurityMode.Standard;
    }
}
