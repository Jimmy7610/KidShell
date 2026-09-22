using KidShell.Core.Runtime;
using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Security.Transactions;

/// <summary>
/// One reason a machine is not armed. Every condition is listed separately so
/// the UI can say exactly what is missing rather than "not allowed".
/// </summary>
public enum ArmingRequirement
{
    /// <summary>The build has no security feature compiled in at all.</summary>
    SecurityFeatureNotBuilt = 0,

    /// <summary>The process is not elevated.</summary>
    NotElevated = 1,

    /// <summary>No parent authorised this, or the authorisation expired.</summary>
    ParentNotAuthorized = 2,

    /// <summary>Recovery preflight did not pass.</summary>
    RecoveryNotProven = 3,

    /// <summary>The parent has not confirmed this specific transaction.</summary>
    TransactionNotConfirmed = 4,

    /// <summary>The machine cannot do what the transaction requires.</summary>
    CapabilityMissing = 5,

    /// <summary>The device has not been marked as a test or production target.</summary>
    DeviceNotDesignated = 6,

    /// <summary>A parent PIN has not been configured.</summary>
    ParentPinMissing = 7
}

/// <summary>Whether Apply may be constructed, and if not, why not.</summary>
public sealed record ArmingDecision
{
    public required bool IsArmed { get; init; }

    public IReadOnlyList<ArmingRequirement> Unmet { get; init; } = [];

    /// <summary>Parent-facing summary, Swedish.</summary>
    public string Summary { get; init; } = string.Empty;

    public static ArmingDecision Denied(params ArmingRequirement[] unmet) => new()
    {
        IsArmed = false,
        Unmet = unmet,
        Summary = "KidShell får inte ändra Windows på den här datorn."
    };
}

/// <summary>
/// The half of arming that is genuinely a human decision.
///
/// These two are caller-supplied because they have to be: no amount of reading
/// the machine can tell you whether a parent understood what they agreed to.
/// They are also the two that are useless on their own — asserting both still
/// arms nothing, because the machine-derived half cannot be asserted at all.
/// See <see cref="VerifiedMachineFacts"/>.
/// </summary>
public sealed record ParentAuthorization
{
    /// <summary>The parent authenticated, recently, for this purpose.</summary>
    public required bool Authenticated { get; init; }

    /// <summary>The parent confirmed this exact transaction after reading it.</summary>
    public required bool TransactionConfirmed { get; init; }

    /// <summary>Nobody has authorised anything. The default everywhere.</summary>
    public static ParentAuthorization None { get; } = new()
    {
        Authenticated = false,
        TransactionConfirmed = false
    };
}

/// <summary>
/// The half of arming that is a fact about the machine, and therefore not
/// something a caller is allowed to claim.
///
/// WHY THIS IS A CLASS AND NOT FIVE BOOLEANS
/// -----------------------------------------
/// It used to be five <c>required bool</c> properties on the arming request,
/// which meant the security authority was whatever the last caller typed.
/// <c>new ArmingRequest { ProcessElevated = true, RecoveryProven = true, … }</c>
/// was a complete, compiling assertion that the machine was ready — and the
/// day an Apply factory exists, that one expression would have been the whole
/// distance between "audit only" and "changing Windows".
///
/// So the type has a private constructor, get-only properties and no public
/// factory that produces anything but <see cref="NothingVerified"/>. The only
/// way to obtain facts that are actually true is
/// <see cref="MachineFactVerifier.Verify"/>, which reads them from a
/// capability analysis of the real machine and from source interfaces that
/// **have no implementation anywhere in KidShell**. A structural test asserts
/// that, so writing the first one is a deliberate, reviewable act.
///
/// A caller may still lie about the human half. That is fine and unavoidable:
/// lying about parent consent gets you a decision object, and the machine half
/// still reads false.
/// </summary>
public sealed class VerifiedMachineFacts
{
    private VerifiedMachineFacts(
        bool processElevated,
        bool parentPinConfigured,
        bool deviceDesignated,
        bool recoveryProven,
        bool capabilitySatisfied)
    {
        ProcessElevated = processElevated;
        ParentPinConfigured = parentPinConfigured;
        DeviceDesignated = deviceDesignated;
        RecoveryProven = recoveryProven;
        CapabilitySatisfied = capabilitySatisfied;
    }

    /// <summary>This process is running elevated right now.</summary>
    public bool ProcessElevated { get; }

    public bool ParentPinConfigured { get; }

    /// <summary>
    /// The device was explicitly designated as a KidShell target. A
    /// development machine never is, and the verifier enforces that rather
    /// than trusting the source.
    /// </summary>
    public bool DeviceDesignated { get; }

    /// <summary>Recovery preflight passed and a manifest could be written.</summary>
    public bool RecoveryProven { get; }

    /// <summary>The machine can do what the transaction needs.</summary>
    public bool CapabilitySatisfied { get; }

    /// <summary>
    /// Nothing has been verified. The only instance obtainable without going
    /// through the verifier, and the safe default: every condition false.
    /// </summary>
    public static VerifiedMachineFacts NothingVerified { get; } =
        new(false, false, false, false, false);

    /// <summary>
    /// The single construction site for facts that are not all false. Internal
    /// so the verifier owns it; nothing else in Core calls it.
    /// </summary>
    internal static VerifiedMachineFacts Verified(
        bool processElevated,
        bool parentPinConfigured,
        bool deviceDesignated,
        bool recoveryProven,
        bool capabilitySatisfied) =>
        new(processElevated, parentPinConfigured, deviceDesignated, recoveryProven, capabilitySatisfied);
}

/// <summary>
/// Answers whether this device was designated a KidShell target.
///
/// Deliberately not implemented. Designation is meant to come from an
/// installer acting on a decision made outside the app, precisely so that no
/// code path inside KidShell can designate the machine it is running on.
/// </summary>
public interface IDeviceDesignationSource
{
    bool IsDesignatedKidShellDevice { get; }
}

/// <summary>
/// Answers whether there is a proven way back: recovery preflight passed and
/// a recovery manifest could actually be written to disk.
///
/// Deliberately not implemented.
/// </summary>
public interface IRecoveryReadinessSource
{
    bool IsRecoveryProven { get; }
}

/// <summary>
/// Answers whether a real parent PIN has been configured.
///
/// Deliberately not implemented — the configuration layer knows this, but
/// wiring it up is part of the milestone that adds Apply, not this one.
/// </summary>
public interface IParentPinSource
{
    bool IsParentPinConfigured { get; }
}

/// <summary>
/// Turns machine readings into <see cref="VerifiedMachineFacts"/>.
///
/// This is the trust boundary. Everything on the far side of it is read from
/// the machine or from a dedicated source; nothing is taken on a caller's
/// word. Two of the five conditions are not even asked of a source:
///
///  * elevation comes from the capability analysis, which reads the process
///    token, because a caller claiming to be elevated is worth nothing;
///  * capability satisfaction is computed from what the transaction actually
///    requires against what the machine actually supports, rather than being
///    summarised into a boolean by whoever built the request.
///
/// And designation is refused outright on a development build, whatever the
/// source says. A machine somebody is writing KidShell on is not a machine
/// KidShell may lock down, and that decision does not belong to a source
/// implementation that could get it wrong.
/// </summary>
public static class MachineFactVerifier
{
    public static VerifiedMachineFacts Verify(
        IRuntimeEnvironment environment,
        WindowsSecurityCapabilities capabilities,
        IDeviceDesignationSource designation,
        IRecoveryReadinessSource recovery,
        IParentPinSource parentPin,
        IReadOnlyCollection<RequiredCapability> required)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(designation);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(parentPin);
        ArgumentNullException.ThrowIfNull(required);

        // A development machine is never a target, regardless of what any
        // source claims.
        var designated = environment.IsProduction && designation.IsDesignatedKidShellDevice;

        return VerifiedMachineFacts.Verified(
            processElevated: capabilities.IsProcessElevated,
            parentPinConfigured: parentPin.IsParentPinConfigured,
            deviceDesignated: designated,
            recoveryProven: recovery.IsRecoveryProven,
            capabilitySatisfied: required.All(r => Supports(capabilities, r)));
    }

    /// <summary>
    /// Whether the machine supports one required capability. Unknown counts as
    /// unsupported — an undetermined reading is not permission.
    /// </summary>
    public static bool Supports(WindowsSecurityCapabilities capabilities, RequiredCapability required)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return required switch
        {
            RequiredCapability.None => true,
            RequiredCapability.AssignedAccess => capabilities.SupportsAssignedAccess,
            RequiredCapability.AppLockerEnforcement => capabilities.SupportsAppLockerEnforcement,
            RequiredCapability.AppLockerDeployment => capabilities.SupportsAppLockerDeployment,
            RequiredCapability.KidShellAppAllowlist =>
                capabilities.KidShellAppAllowlist == CapabilityState.Available,

            // Managing local accounts needs an elevated administrator; there
            // is no edition gate on it.
            RequiredCapability.LocalAccountManagement => capabilities.IsProcessElevated,

            // An unrecognised capability is not a satisfied one.
            _ => false
        };
    }
}

/// <summary>
/// Decides whether KidShell may change Windows, and builds the context if so.
///
/// Seven conditions, all required. They are not a checklist for its own sake —
/// each one removes a specific way the machine could be changed by accident:
///
///   * a Release build that nobody meant to run in this mode;
///   * an unelevated process that would half-apply and fail;
///   * a child who reached the screen;
///   * a parent who pressed the button without reading;
///   * a development machine being treated as a target;
///   * a transaction with no proven way back;
///   * an edition that cannot do what was asked.
///
/// THE TRUST BOUNDARY
/// ------------------
/// The seven arrive as two different kinds of thing, and the split is the
/// point. <see cref="ParentAuthorization"/> is what a human decided, and a
/// caller supplies it because only a human can. <see cref="VerifiedMachineFacts"/>
/// is what is true of the machine, and a caller cannot supply it at all —
/// there is no constructor, and the only non-empty instances come from
/// <see cref="MachineFactVerifier"/> reading the machine through interfaces
/// nothing in KidShell implements.
///
/// So the old failure mode is gone: there is no longer any expression a future
/// caller can write that asserts the machine is ready.
///
/// THE STRUCTURAL GUARANTEE
/// ------------------------
/// Even with all seven met, this class still cannot produce an Apply context.
/// <see cref="SecurityExecutionContext"/> has a private constructor and one
/// public factory returning AuditOnly, and a future milestone must add an
/// Apply factory deliberately. <see cref="TryArm"/> therefore returns the
/// decision and an AuditOnly context — it tells you that you WOULD be allowed,
/// which is what the UI needs, without being the thing that allows it.
///
/// KidShell also compiles with no security feature switch defined, so
/// <see cref="SecurityFeatureCompiledIn"/> is false and the first requirement
/// fails before any of the others are considered.
/// </summary>
public static class SecurityArming
{
    /// <summary>
    /// Whether this build contains any machine-changing capability at all.
    ///
    /// Hard false for the whole 0.x line. It is a constant rather than a
    /// configuration value so the compiler can see it, and so that flipping it
    /// is a code change that shows up in a diff.
    /// </summary>
    public const bool SecurityFeatureCompiledIn = false;

    /// <summary>
    /// Evaluates every condition. Returns the decision and a context that is
    /// always AuditOnly, because no other kind can be constructed.
    /// </summary>
    public static (ArmingDecision Decision, SecurityExecutionContext Context) TryArm(
        ParentAuthorization parent,
        VerifiedMachineFacts machine,
        IRuntimeEnvironment environment,
        WindowsSecurityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(capabilities);

        var unmet = new List<ArmingRequirement>();

        // First and decisive: this build has no mutation capability compiled
        // in, so nothing else can make it armed.
        if (!SecurityFeatureCompiledIn)
        {
            unmet.Add(ArmingRequirement.SecurityFeatureNotBuilt);
        }

        if (!machine.ProcessElevated)
        {
            unmet.Add(ArmingRequirement.NotElevated);
        }

        if (!parent.Authenticated)
        {
            unmet.Add(ArmingRequirement.ParentNotAuthorized);
        }

        if (!machine.ParentPinConfigured)
        {
            unmet.Add(ArmingRequirement.ParentPinMissing);
        }

        if (!machine.RecoveryProven)
        {
            unmet.Add(ArmingRequirement.RecoveryNotProven);
        }

        if (!parent.TransactionConfirmed)
        {
            unmet.Add(ArmingRequirement.TransactionNotConfirmed);
        }

        if (!machine.DeviceDesignated)
        {
            unmet.Add(ArmingRequirement.DeviceNotDesignated);
        }

        if (!machine.CapabilitySatisfied)
        {
            unmet.Add(ArmingRequirement.CapabilityMissing);
        }

        var decision = unmet.Count == 0
            ? new ArmingDecision
            {
                IsArmed = true,
                Summary = "Alla villkor är uppfyllda."
            }
            : ArmingDecision.Denied([.. unmet]);

        // Always AuditOnly. Being armed means "you would be allowed"; it does
        // not hand over the ability, because that ability does not exist.
        return (decision, SecurityExecutionContext.AuditOnly());
    }

    /// <summary>
    /// The conditions as a parent should read them. Used by the Säkerhet page
    /// so the UI never invents its own wording for a security rule.
    /// </summary>
    public static string Describe(ArmingRequirement requirement) => requirement switch
    {
        ArmingRequirement.SecurityFeatureNotBuilt =>
            "Den här versionen av KidShell kan inte ändra Windows.",
        ArmingRequirement.NotElevated =>
            "KidShell körs utan administratörsbehörighet.",
        ArmingRequirement.ParentNotAuthorized =>
            "Ingen vuxen har godkänt ändringen.",
        ArmingRequirement.ParentPinMissing =>
            "Ingen föräldra-PIN är inställd.",
        ArmingRequirement.RecoveryNotProven =>
            "Det finns ingen bekräftad väg tillbaka om något går fel.",
        ArmingRequirement.TransactionNotConfirmed =>
            "Ändringen har inte bekräftats.",
        ArmingRequirement.CapabilityMissing =>
            "Den här datorn saknar funktioner som ändringen kräver.",
        ArmingRequirement.DeviceNotDesignated =>
            "Datorn är inte markerad som en KidShell-dator.",
        _ => "Okänt villkor."
    };
}
