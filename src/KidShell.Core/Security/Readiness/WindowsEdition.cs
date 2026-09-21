namespace KidShell.Core.Security.Readiness;

/// <summary>
/// Normalized Windows edition.
///
/// This is deliberately a small closed set rather than the raw EditionID
/// string: capability decisions elsewhere switch on this, and an unrecognised
/// edition must land on <see cref="Unknown"/> so it can fail safe.
/// </summary>
public enum WindowsEdition
{
    /// <summary>Could not be determined. Treated as the least capable option.</summary>
    Unknown = 0,

    Home = 1,
    Pro = 2,
    ProEducation = 3,
    ProForWorkstations = 4,
    Enterprise = 5,
    Education = 6,
    IoTEnterprise = 7,
    Server = 8
}

/// <summary>Windows generation, derived from the build number rather than a product name.</summary>
public enum WindowsGeneration
{
    Unknown = 0,

    /// <summary>Anything older than Windows 10. Not supported by KidShell.</summary>
    Legacy = 1,

    Windows10 = 2,
    Windows11 = 3
}

/// <summary>
/// How much of KidShell's security story a machine is running.
/// </summary>
public enum SecurityMode
{
    /// <summary>
    /// KidShell UI only. Nothing about Windows is changed and the child can
    /// still leave the app. This is what every 0.1.x build runs.
    /// </summary>
    Development = 0,

    /// <summary>
    /// For editions without Assigned Access. A separate standard child
    /// account, KidShell's own app allowlist, UAC separation and autostart.
    /// Weaker than <see cref="Secure"/> and never described as equivalent.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Pro and above: everything in Standard plus a restricted user
    /// experience through Assigned Access.
    /// </summary>
    Secure = 2
}

public static class WindowsEditionMap
{
    /// <summary>First build number of Windows 11.</summary>
    public const int Windows11MinimumBuild = 22000;

    /// <summary>First build number of Windows 10.</summary>
    public const int Windows10MinimumBuild = 10240;

    /// <summary>
    /// Maps the registry EditionID to a normalized edition.
    ///
    /// EditionID is used rather than ProductName on purpose: on Windows 11 the
    /// ProductName value still reads "Windows 10 ..." for compatibility, so
    /// anything derived from it would be wrong on every Windows 11 machine.
    /// </summary>
    public static WindowsEdition FromEditionId(string? editionId) =>
        (editionId ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            // Home. "Core" is the internal name for the Home SKU.
            "core" or "coren" or "coresinglelanguage" or "corecountryspecific" => WindowsEdition.Home,

            "professional" or "professionaln" => WindowsEdition.Pro,
            "professionaleducation" or "professionaleducationn" => WindowsEdition.ProEducation,
            "professionalworkstation" or "professionalworkstationn" => WindowsEdition.ProForWorkstations,

            "enterprise" or "enterprisen" or "enterprises" or "enterprisesn" => WindowsEdition.Enterprise,
            "education" or "educationn" => WindowsEdition.Education,

            "iotenterprise" or "iotenterprises" => WindowsEdition.IoTEnterprise,

            var id when id.StartsWith("server", StringComparison.Ordinal) => WindowsEdition.Server,

            // Anything unrecognised is Unknown, never a guess at Pro.
            _ => WindowsEdition.Unknown
        };

    public static WindowsGeneration FromBuild(int build) => build switch
    {
        >= Windows11MinimumBuild => WindowsGeneration.Windows11,
        >= Windows10MinimumBuild => WindowsGeneration.Windows10,
        > 0 => WindowsGeneration.Legacy,
        _ => WindowsGeneration.Unknown
    };

    /// <summary>Swedish-neutral edition name, e.g. "Pro" or "Home".</summary>
    public static string EditionName(WindowsEdition edition) => edition switch
    {
        WindowsEdition.Home => "Home",
        WindowsEdition.Pro => "Pro",
        WindowsEdition.ProEducation => "Pro Education",
        WindowsEdition.ProForWorkstations => "Pro for Workstations",
        WindowsEdition.Enterprise => "Enterprise",
        WindowsEdition.Education => "Education",
        WindowsEdition.IoTEnterprise => "IoT Enterprise",
        WindowsEdition.Server => "Server",
        _ => "okänd utgåva"
    };

    public static string GenerationName(WindowsGeneration generation) => generation switch
    {
        WindowsGeneration.Windows11 => "Windows 11",
        WindowsGeneration.Windows10 => "Windows 10",
        WindowsGeneration.Legacy => "Äldre Windows",
        _ => "Windows"
    };

    /// <summary>Human-readable name, e.g. "Windows 11 Home".</summary>
    public static string DisplayName(WindowsGeneration generation, WindowsEdition edition) =>
        edition == WindowsEdition.Unknown
            ? $"{GenerationName(generation)} (okänd utgåva)"
            : $"{GenerationName(generation)} {EditionName(edition)}";
}
