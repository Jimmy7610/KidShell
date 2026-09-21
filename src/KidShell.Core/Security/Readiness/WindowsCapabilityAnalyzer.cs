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
    /// Editions where AppLocker is a supported, enforceable feature.
    ///
    /// Narrower than <see cref="AssignedAccessEditions"/>: Pro can author
    /// AppLocker rules but Microsoft supports enforcement only on Enterprise,
    /// Education and IoT Enterprise. KidShell reports what is supported, not
    /// what can be made to run.
    /// </summary>
    private static readonly HashSet<WindowsEdition> AppLockerEditions =
    [
        WindowsEdition.Enterprise,
        WindowsEdition.Education,
        WindowsEdition.IoTEnterprise
    ];

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

        var appLocker = unknownPlatform
            ? CapabilityState.Unknown
            : AppLockerEditions.Contains(edition) ? CapabilityState.Available : CapabilityState.Unavailable;

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
            AppLocker = appLocker,
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
