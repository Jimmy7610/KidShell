using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Security.AppControl;

/// <summary>
/// A documented way to install an application-control policy.
///
/// Every member here corresponds to a mechanism Microsoft documents. There is
/// deliberately no "direct registry write" member: writing to SrpV2 by hand is
/// undocumented, unsupported, and would be exactly the kind of hack that makes
/// a security product untrustworthy.
/// </summary>
public enum DeploymentChannel
{
    /// <summary>Set-AppLockerPolicy from the AppLocker PowerShell module.</summary>
    PowerShellModule = 0,

    /// <summary>The AppLocker CSP, used by MDM. Pro and above.</summary>
    ConfigurationServiceProvider = 1,

    /// <summary>Local Security Policy / Group Policy console.</summary>
    LocalSecurityPolicy = 2,

    /// <summary>Domain Group Policy.</summary>
    GroupPolicy = 3
}

/// <summary>Whether a channel can be used here.</summary>
public sealed record DeploymentChannelStatus
{
    public required DeploymentChannel Channel { get; init; }

    public required bool IsAvailable { get; init; }

    /// <summary>Parent-facing reason when unavailable, Swedish.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Whether using it needs an elevated process.</summary>
    public bool RequiresAdministrator { get; init; } = true;
}

/// <summary>
/// Installs a generated policy.
///
/// Declared, and deliberately left with no implementation. Deploying an
/// application-control policy is the single most consequential thing KidShell
/// could ever do to a machine: a wrong rule set means the child - or the
/// parent - cannot start anything, including the tool that would fix it.
///
/// A test asserts no type implements this. When a future milestone writes the
/// first one it must go through <see cref="Transactions.SecurityTransaction"/>
/// like any other change, with a snapshot and a proven rollback.
/// </summary>
public interface IAppControlDeploymentChannel
{
    DeploymentChannel Channel { get; }

    Task<DeploymentChannelStatus> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Works out which deployment channels a machine actually has.
///
/// Pure, and deliberately blunt about the answer. On a stock Windows Home
/// machine every channel is unavailable: the AppLocker PowerShell module is
/// not installed, the policy consoles do not exist, and the CSP needs Pro. The
/// correct output there is "no supported way to install a policy", not a
/// fallback that pretends otherwise.
/// </summary>
public static class DeploymentChannelPlanner
{
    public static IReadOnlyList<DeploymentChannelStatus> Evaluate(WindowsSecurityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var appControl = capabilities.AppControl;

        return
        [
            new DeploymentChannelStatus
            {
                Channel = DeploymentChannel.PowerShellModule,
                IsAvailable = appControl.PowerShellManagement == CapabilityState.Available,
                Reason = appControl.PowerShellManagement == CapabilityState.Available
                    ? string.Empty
                    : "AppLockers PowerShell-modul finns inte på den här datorn."
            },
            new DeploymentChannelStatus
            {
                Channel = DeploymentChannel.ConfigurationServiceProvider,
                IsAvailable = appControl.Csp == CapabilityState.Available,
                Reason = appControl.Csp == CapabilityState.Available
                    ? string.Empty
                    : "AppLocker CSP kräver Windows Pro eller senare."
            },
            new DeploymentChannelStatus
            {
                Channel = DeploymentChannel.LocalSecurityPolicy,
                IsAvailable = appControl.ManagementUi == CapabilityState.Available,
                Reason = appControl.ManagementUi == CapabilityState.Available
                    ? string.Empty
                    : "Den lokala säkerhetsprincipen (secpol.msc) finns inte i den här Windows-utgåvan."
            },
            new DeploymentChannelStatus
            {
                Channel = DeploymentChannel.GroupPolicy,

                // A domain-joined machine is not a scenario KidShell targets,
                // and claiming it without detecting a domain would be a guess.
                IsAvailable = false,
                Reason = "Grupprincip används bara på datorer som hör till en domän."
            }
        ];
    }

    /// <summary>
    /// The channel a future milestone would use, or null when there is none.
    ///
    /// Null is a legitimate, expected answer on Windows Home, and callers must
    /// report it rather than substituting an unsupported mechanism.
    /// </summary>
    public static DeploymentChannel? Recommend(WindowsSecurityCapabilities capabilities)
    {
        var channels = Evaluate(capabilities);

        // Ordered by how well supported and how reversible each is.
        foreach (var preferred in new[]
                 {
                     DeploymentChannel.PowerShellModule,
                     DeploymentChannel.ConfigurationServiceProvider,
                     DeploymentChannel.LocalSecurityPolicy
                 })
        {
            if (channels.Any(c => c.Channel == preferred && c.IsAvailable))
            {
                return preferred;
            }
        }

        return null;
    }

    /// <summary>
    /// The honest summary for the Säkerhet page.
    ///
    /// The awkward case is stated plainly rather than rounded off: a machine
    /// that would enforce a policy but has no way to receive one.
    /// </summary>
    public static string Summarize(WindowsSecurityCapabilities capabilities)
    {
        var recommended = Recommend(capabilities);

        if (recommended is not null)
        {
            return recommended switch
            {
                DeploymentChannel.PowerShellModule => "Regler kan installeras med AppLockers PowerShell-modul.",
                DeploymentChannel.ConfigurationServiceProvider => "Regler kan installeras via AppLocker CSP.",
                DeploymentChannel.LocalSecurityPolicy => "Regler kan installeras via den lokala säkerhetsprincipen.",
                _ => "Regler kan installeras."
            };
        }

        if (capabilities.AppControl.CanEnforce)
        {
            return "Windows kan spärra program på den här datorn, men det finns inget inbyggt " +
                   "sätt att installera reglerna här. KidShell använder inte odokumenterade genvägar.";
        }

        return "Den här datorn stöder inte appkontroll i Windows.";
    }
}
