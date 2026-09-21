namespace KidShell.Core.Security.Readiness;

/// <summary>
/// What this machine can do with AppLocker, split by concern.
///
/// A single "SupportsAppLocker" boolean was wrong in both directions. It said
/// Home could not enforce AppLocker, which stopped being true with KB 5024351,
/// and it implied that being able to enforce meant being able to deploy — two
/// separate questions with different answers on the same machine.
///
/// Microsoft's current documentation separates them, and so does this:
///
///  * ENFORCEMENT is a property of the Windows version. Since KB 5024351,
///    Windows 10 2004+ and all Windows 11 versions enforce AppLocker policies
///    on every edition, Home included.
///  * DEPLOYMENT is a property of the edition and of what is installed. The
///    AppLocker CSP needs Pro or above; the PowerShell module and the Local
///    Security Policy console are not present on every machine.
///
/// A machine can therefore enforce a policy it has no supported way to
/// install, which is exactly the case on Windows 11 Home.
/// </summary>
public sealed record AppControlCapabilities
{
    /// <summary>
    /// Whether this Windows version enforces AppLocker policies at all.
    /// Edition-independent on Windows 10 2004+ and Windows 11.
    /// </summary>
    public required CapabilityState Enforcement { get; init; }

    /// <summary>
    /// Application Identity (AppIDSvc), the service that actually evaluates
    /// AppLocker rules. Present on this machine or not.
    /// </summary>
    public required CapabilityState EnforcementService { get; init; }

    /// <summary>Service start mode, for diagnostics. Never changed by KidShell.</summary>
    public string EnforcementServiceStartMode { get; init; } = string.Empty;

    /// <summary>The AppLocker PowerShell module and cmdlets, on this machine.</summary>
    public required CapabilityState PowerShellManagement { get; init; }

    /// <summary>Whether Get-AppLockerPolicy -Local could actually be read.</summary>
    public required CapabilityState LocalPolicyReadable { get; init; }

    /// <summary>The local policy store directory under System32.</summary>
    public required CapabilityState LocalPolicyStore { get; init; }

    /// <summary>secpol.msc / gpedit.msc. Absent on Home.</summary>
    public required CapabilityState ManagementUi { get; init; }

    /// <summary>
    /// The AppLocker configuration service provider, used by MDM. Documented
    /// for Pro, Enterprise, Education and IoT Enterprise — not Home.
    /// </summary>
    public required CapabilityState Csp { get; init; }

    /// <summary>
    /// Whether the machine could enforce a policy: the Windows version
    /// supports it and the enforcement service exists.
    /// </summary>
    public bool CanEnforce =>
        Enforcement == CapabilityState.Available &&
        EnforcementService == CapabilityState.Available;

    /// <summary>
    /// Whether KidShell has any supported way to install a policy here.
    ///
    /// False on a stock Home machine: enforcement works, but the PowerShell
    /// module, the policy console and the CSP are all absent. Planning around
    /// this is MVP 0.2's problem; reporting it honestly is this milestone's.
    /// </summary>
    public bool HasDeploymentChannel =>
        PowerShellManagement == CapabilityState.Available ||
        ManagementUi == CapabilityState.Available ||
        Csp == CapabilityState.Available;

    /// <summary>
    /// The interesting case, and the one this machine is in: the rules would
    /// be enforced if they could be installed, and there is no supported way
    /// to install them.
    /// </summary>
    public bool CanEnforceButCannotDeploy => CanEnforce && !HasDeploymentChannel;

    /// <summary>Everything unknown. Used when detection failed outright.</summary>
    public static AppControlCapabilities Unknown { get; } = new()
    {
        Enforcement = CapabilityState.Unknown,
        EnforcementService = CapabilityState.Unknown,
        PowerShellManagement = CapabilityState.Unknown,
        LocalPolicyReadable = CapabilityState.Unknown,
        LocalPolicyStore = CapabilityState.Unknown,
        ManagementUi = CapabilityState.Unknown,
        Csp = CapabilityState.Unknown
    };
}
