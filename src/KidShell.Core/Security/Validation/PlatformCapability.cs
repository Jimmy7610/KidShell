namespace KidShell.Core.Security.Validation;

/// <summary>How much of a Windows feature this edition can actually do.</summary>
public enum CapabilitySupport
{
    NotSupported = 0,
    PartiallySupported = 1,
    Supported = 2
}

/// <summary>A capability answer with the reason in plain language.</summary>
public sealed record CapabilityVerdict(CapabilitySupport Support, string Reason)
{
    public string Label => Support switch
    {
        CapabilitySupport.Supported => "SUPPORTED",
        CapabilitySupport.PartiallySupported => "PARTIALLY_SUPPORTED",
        _ => "NOT_SUPPORTED"
    };
}

/// <summary>What a read-only probe found out about this Windows.</summary>
public sealed record PlatformFacts
{
    /// <summary>The EditionID from the registry: Core, Professional, Enterprise…</summary>
    public string EditionId { get; init; } = string.Empty;

    /// <summary>ProductName, for the human-readable reason.</summary>
    public string ProductName { get; init; } = string.Empty;

    public int Build { get; init; }

    /// <summary>Whether the Application Identity service exists at all.</summary>
    public bool AppIdServicePresent { get; init; }

    public string AppIdServiceStartType { get; init; } = string.Empty;

    /// <summary>Whether the AppLocker cmdlets are available in this session.</summary>
    public bool AppLockerCmdletsPresent { get; init; }

    /// <summary>Whether an effective AppLocker policy could be read.</summary>
    public bool AppLockerPolicyReadable { get; init; }

    /// <summary>Whether the Assigned Access configuration surface is present.</summary>
    public bool AssignedAccessCmdletsPresent { get; init; }

    /// <summary>Whether this edition offers the multi-app kiosk KidShell needs.</summary>
    public bool MultiAppKioskAvailable { get; init; }
}

/// <summary>
/// Whether this Windows can enforce AppLocker, and whether it can only pretend
/// to.
///
/// THE ANSWER WINDOWS HOME GETS IS NO
/// ----------------------------------
/// Home has no supported deployment channel for AppLocker: no Group Policy
/// editor, and the MDM path is not available either. The cmdlets may be
/// present, a policy may even be readable, and none of that makes enforcement
/// something a parent can rely on. Saying SUPPORTED there would be the single
/// most damaging overclaim this product could make, because a parent who
/// believes the computer blocks unapproved programs supervises less.
/// </summary>
public static class AppLockerCapability
{
    /// <summary>Editions with a supported way to deploy a policy.</summary>
    private static readonly string[] CapableEditions =
    [
        "Professional", "ProfessionalN", "ProfessionalWorkstation",
        "Enterprise", "EnterpriseN", "EnterpriseS",
        "Education", "EducationN",
        "IoTEnterprise", "IoTEnterpriseS",
        "ServerStandard", "ServerDatacenter"
    ];

    public static CapabilityVerdict Evaluate(PlatformFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var editionCapable = CapableEditions.Contains(facts.EditionId, StringComparer.OrdinalIgnoreCase);

        if (!editionCapable)
        {
            return new CapabilityVerdict(CapabilitySupport.NotSupported,
                $"{Describe(facts)} has no supported channel for deploying an AppLocker policy. " +
                "KidShell can generate and review one; this edition cannot enforce it.");
        }

        if (!facts.AppIdAvailableForEnforcement())
        {
            return new CapabilityVerdict(CapabilitySupport.PartiallySupported,
                $"{Describe(facts)} can enforce AppLocker, and the Application Identity service " +
                $"is {(facts.AppIdServicePresent ? $"set to {facts.AppIdServiceStartType}" : "absent")}. " +
                "Rules do nothing until that service runs, so enforcement is not active yet.");
        }

        if (!facts.AppLockerCmdletsPresent)
        {
            return new CapabilityVerdict(CapabilitySupport.PartiallySupported,
                $"{Describe(facts)} can enforce AppLocker, and the AppLocker cmdlets are not " +
                "available in this session, so a policy cannot be deployed or read from here.");
        }

        return new CapabilityVerdict(CapabilitySupport.Supported,
            $"{Describe(facts)} supports AppLocker, the Application Identity service is " +
            $"{facts.AppIdServiceStartType}, and a policy can be read" +
            $"{(facts.AppLockerPolicyReadable ? "" : " (none is currently effective)")}.");
    }

    private static bool AppIdAvailableForEnforcement(this PlatformFacts facts) =>
        facts.AppIdServicePresent &&
        facts.AppIdServiceStartType.Contains("Auto", StringComparison.OrdinalIgnoreCase);

    internal static string Describe(PlatformFacts facts) =>
        string.IsNullOrWhiteSpace(facts.ProductName)
            ? $"Edition '{facts.EditionId}'"
            : $"{facts.ProductName} ({facts.EditionId})";
}

/// <summary>
/// Whether this Windows can put the child in a restricted shell.
///
/// Same honesty rule as AppLocker, and the same edition line: Home cannot do
/// Assigned Access, and KidShell says so rather than failing oddly later.
/// </summary>
public static class AssignedAccessCapability
{
    private static readonly string[] CapableEditions =
    [
        "Professional", "ProfessionalN", "ProfessionalWorkstation",
        "Enterprise", "EnterpriseN", "EnterpriseS",
        "Education", "EducationN",
        "IoTEnterprise", "IoTEnterpriseS"
    ];

    /// <summary>Multi-app kiosk needs Windows 10 1809 or later.</summary>
    public const int MinimumBuild = 17763;

    public static CapabilityVerdict Evaluate(PlatformFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!CapableEditions.Contains(facts.EditionId, StringComparer.OrdinalIgnoreCase))
        {
            return new CapabilityVerdict(CapabilitySupport.NotSupported,
                $"{AppLockerCapability.Describe(facts)} does not offer Assigned Access. " +
                "Secure Mode's restricted shell is unavailable on this edition; " +
                "Standard Mode is what a parent gets.");
        }

        if (facts.Build is > 0 and < MinimumBuild)
        {
            return new CapabilityVerdict(CapabilitySupport.NotSupported,
                $"Build {facts.Build} is older than {MinimumBuild}, which is where the " +
                "multi-app restricted experience begins.");
        }

        if (!facts.MultiAppKioskAvailable)
        {
            return new CapabilityVerdict(CapabilitySupport.PartiallySupported,
                $"{AppLockerCapability.Describe(facts)} supports Assigned Access, and the " +
                "multi-app configuration KidShell needs could not be confirmed. Single-app " +
                "kiosk is not what this product wants: a child needs more than one program.");
        }

        if (!facts.AssignedAccessCmdletsPresent)
        {
            return new CapabilityVerdict(CapabilitySupport.PartiallySupported,
                $"{AppLockerCapability.Describe(facts)} supports Assigned Access, and the " +
                "configuration surface is not reachable from this session.");
        }

        return new CapabilityVerdict(CapabilitySupport.Supported,
            $"{AppLockerCapability.Describe(facts)} supports the multi-app Assigned Access " +
            "configuration KidShell's Secure Mode uses.");
    }
}
