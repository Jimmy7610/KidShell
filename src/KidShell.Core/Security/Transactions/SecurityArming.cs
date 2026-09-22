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
/// What a caller asserts when asking to arm.
///
/// Every field is something a human did, not something the app inferred. That
/// is the point: arming is a decision somebody made, and each part of it has
/// to be presented separately so none of them can be assumed.
/// </summary>
public sealed record ArmingRequest
{
    /// <summary>The parent authenticated, recently, for this purpose.</summary>
    public required bool ParentAuthenticated { get; init; }

    /// <summary>The parent confirmed this exact transaction after reading it.</summary>
    public required bool TransactionConfirmed { get; init; }

    /// <summary>
    /// The device was explicitly designated as a KidShell target. A
    /// development machine never is, and there is no way to designate one
    /// from inside the app.
    /// </summary>
    public required bool DeviceDesignated { get; init; }

    /// <summary>Recovery preflight passed and a manifest was written.</summary>
    public required bool RecoveryProven { get; init; }

    public required bool ProcessElevated { get; init; }

    public required bool ParentPinConfigured { get; init; }

    /// <summary>The machine can do what the transaction needs.</summary>
    public required bool CapabilitySatisfied { get; init; }
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
        ArmingRequest request,
        IRuntimeEnvironment environment,
        WindowsSecurityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(capabilities);

        var unmet = new List<ArmingRequirement>();

        // First and decisive: this build has no mutation capability compiled
        // in, so nothing else can make it armed.
        if (!SecurityFeatureCompiledIn)
        {
            unmet.Add(ArmingRequirement.SecurityFeatureNotBuilt);
        }

        if (!request.ProcessElevated)
        {
            unmet.Add(ArmingRequirement.NotElevated);
        }

        if (!request.ParentAuthenticated)
        {
            unmet.Add(ArmingRequirement.ParentNotAuthorized);
        }

        if (!request.ParentPinConfigured)
        {
            unmet.Add(ArmingRequirement.ParentPinMissing);
        }

        if (!request.RecoveryProven)
        {
            unmet.Add(ArmingRequirement.RecoveryNotProven);
        }

        if (!request.TransactionConfirmed)
        {
            unmet.Add(ArmingRequirement.TransactionNotConfirmed);
        }

        if (!request.DeviceDesignated)
        {
            unmet.Add(ArmingRequirement.DeviceNotDesignated);
        }

        if (!request.CapabilitySatisfied)
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
