namespace KidShell.Core.Security.Broker;

/// <summary>What authority an operation needs before the service will do it.</summary>
public enum BrokerAuthority
{
    /// <summary>
    /// Any authenticated caller. Reserved for operations that reveal nothing
    /// and change nothing.
    /// </summary>
    AnyAuthenticated = 0,

    /// <summary>
    /// The KidShell session, writing enforcement state.
    ///
    /// Allowed because the transition rules make the write one-directional:
    /// this authority can make the child's situation stricter and cannot make
    /// it looser. Without those rules this would be the same as handing the
    /// child the policy.
    /// </summary>
    ChildSessionRestricted = 1,

    /// <summary>
    /// A parent who has authenticated against the service this session.
    ///
    /// The capability is issued by the service after IT verified the PIN.
    /// </summary>
    ParentCapability = 2,

    /// <summary>
    /// An elevated administrator. The only authority that changes policy.
    /// </summary>
    Administrator = 3
}

/// <summary>Whether a request may proceed, and why not when it may not.</summary>
public sealed record BrokerDecision(bool Allowed, BrokerFailureReason Reason, string Explanation)
{
    public static BrokerDecision Allow(string explanation) =>
        new(true, BrokerFailureReason.None, explanation);

    public static BrokerDecision Deny(BrokerFailureReason reason, string explanation) =>
        new(false, reason, explanation);
}

/// <summary>
/// The complete answer to "may this caller ask for this?".
///
/// THE QUESTION THIS TYPE EXISTS TO ANSWER
/// ---------------------------------------
/// What prevents a compromised or modified KidShell.App from calling
/// SaveParentPolicy with an attacker-controlled PIN, app list and web policy?
///
/// Before this pass, nothing did. The operation was internal, the enum was
/// closed, the payload was validated - and none of that is an authority
/// check. A modified KidShell.App is a program running as the child that
/// sends well-formed requests, and every one of those defences would have
/// waved it through.
///
/// So the answer is now a Windows one: SaveParentPolicy, MarkProvisioned and
/// CommitStagedParentPolicy require a caller whose TOKEN is elevated and in
/// the Administrators group, which the child's account is not and cannot
/// become by modifying a program it can write. The child's session may
/// propose a policy, and may advance enforcement state in the stricter
/// direction. It may not decide what the rules are.
///
/// WHAT THIS DOES NOT CLAIM
/// ------------------------
/// It does not claim the caller IS KidShell. A process named KidShell.App.exe
/// is not proof of anything, and no check here looks at a process name.
/// Binary identity needs code signing, which this product does not have yet;
/// see docs/PRIVILEGED-BROKER-SERVICE-2026-09-30.md. What the matrix
/// establishes is which WINDOWS PRINCIPAL is asking, and it refuses to let
/// the child's principal do a parent's work regardless of which program is
/// doing the asking.
/// </summary>
public static class BrokerAuthorizationPolicy
{
    /// <summary>
    /// The authority each operation requires. Total over the enum, so a new
    /// operation does not compile until someone has decided who may call it.
    /// </summary>
    public static BrokerAuthority RequiredFor(ElevatedOperationKind kind) => kind switch
    {
        // Reveals only that the service is running.
        ElevatedOperationKind.Probe => BrokerAuthority.AnyAuthenticated,

        // Enforcement state. One-directional, see the transition rules.
        ElevatedOperationKind.SaveScreenTimeState => BrokerAuthority.ChildSessionRestricted,
        ElevatedOperationKind.SavePinThrottleState => BrokerAuthority.ChildSessionRestricted,

        // A proposal. Nothing reads the staged slot for enforcement.
        ElevatedOperationKind.StageParentPolicy => BrokerAuthority.ChildSessionRestricted,

        // The service does the verifying, so the session may ask - and the
        // service's own throttle is what makes that safe.
        ElevatedOperationKind.VerifyParentPin => BrokerAuthority.ChildSessionRestricted,

        // Same-day, reversible, bounded. A parent who proved the PIN to the
        // service may do these without a consent prompt, because a prompt for
        // "fifteen more minutes" is a prompt parents learn to click through.
        ElevatedOperationKind.GrantScreenTime => BrokerAuthority.ParentCapability,
        ElevatedOperationKind.ResetScreenTimeToday => BrokerAuthority.ParentCapability,

        // Everything that decides what the rules ARE.
        ElevatedOperationKind.SaveParentPolicy => BrokerAuthority.Administrator,
        ElevatedOperationKind.CommitStagedParentPolicy => BrokerAuthority.Administrator,
        ElevatedOperationKind.MarkProvisioned => BrokerAuthority.Administrator,

        // Everything that changes the machine.
        ElevatedOperationKind.CreateChildAccount => BrokerAuthority.Administrator,
        ElevatedOperationKind.DemoteChildAccount => BrokerAuthority.Administrator,
        ElevatedOperationKind.ConfigureAutostart => BrokerAuthority.Administrator,
        ElevatedOperationKind.DeployAppLockerPolicy => BrokerAuthority.Administrator,
        ElevatedOperationKind.ConfigureApplicationIdentityService => BrokerAuthority.Administrator,
        ElevatedOperationKind.ConfigureAssignedAccess => BrokerAuthority.Administrator,
        ElevatedOperationKind.DeployBrowserPolicy => BrokerAuthority.Administrator,
        ElevatedOperationKind.InstallWatchdogService => BrokerAuthority.Administrator,
        ElevatedOperationKind.InstallSecurityHostService => BrokerAuthority.Administrator,

        // Unreachable for a defined member, and the safe answer for one that
        // somebody adds without coming here.
        _ => BrokerAuthority.Administrator
    };

    /// <summary>
    /// Whether this caller may perform this operation.
    ///
    /// <paramref name="hasParentCapability"/> is the SERVICE's own answer to
    /// "did I issue this capability, to this caller, and is it still valid" -
    /// never a field copied out of the request.
    /// </summary>
    public static BrokerDecision Decide(
        BrokerCaller caller,
        ElevatedOperationKind kind,
        bool hasParentCapability)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!Enum.IsDefined(kind))
        {
            return BrokerDecision.Deny(BrokerFailureReason.UnknownOperation,
                "The operation is not one this service was built with.");
        }

        if (!caller.IsAuthenticated)
        {
            // Before anything else. An unidentified caller is refused for
            // every operation including Probe, so the service does not even
            // confirm its own presence to something it cannot name.
            return BrokerDecision.Deny(BrokerFailureReason.NotAuthorized,
                "The caller could not be identified.");
        }

        var required = RequiredFor(kind);

        // LocalSystem is the service's own identity. It is above every other
        // class by construction: anything able to act as LocalSystem has
        // already won, and pretending otherwise would be theatre.
        if (caller.Class == BrokerCallerClass.System)
        {
            return BrokerDecision.Allow("LocalSystem.");
        }

        return required switch
        {
            BrokerAuthority.AnyAuthenticated =>
                BrokerDecision.Allow("Any authenticated caller may ask."),

            BrokerAuthority.ChildSessionRestricted =>
                caller.Class is BrokerCallerClass.ChildSession or BrokerCallerClass.Administrator
                    ? BrokerDecision.Allow("An identified session, within the transition rules.")
                    : BrokerDecision.Deny(BrokerFailureReason.NotAuthorized,
                        "Only an identified session may write enforcement state."),

            BrokerAuthority.ParentCapability =>
                hasParentCapability
                    ? BrokerDecision.Allow("A parent capability this service issued.")
                    : caller.Class == BrokerCallerClass.Administrator && caller.IsElevated
                        ? BrokerDecision.Allow("An elevated administrator.")
                        : BrokerDecision.Deny(BrokerFailureReason.ParentAuthorizationRequired,
                            "A parent must authenticate before this can be done."),

            BrokerAuthority.Administrator =>
                caller.Class == BrokerCallerClass.Administrator && caller.IsElevated
                    ? BrokerDecision.Allow("An elevated administrator.")
                    : BrokerDecision.Deny(BrokerFailureReason.NotAuthorized,
                        "This changes what the rules are, and needs an administrator."),

            _ => BrokerDecision.Deny(BrokerFailureReason.NotAuthorized, "No rule permits this.")
        };
    }

    /// <summary>
    /// Every operation a child's session can reach on its own.
    ///
    /// Derived rather than listed, so it cannot disagree with the matrix, and
    /// exposed so a test can assert the set has not quietly grown.
    /// </summary>
    public static IReadOnlyList<ElevatedOperationKind> ReachableByChildSession =>
        Enum.GetValues<ElevatedOperationKind>()
            .Where(k => Decide(SampleChild, k, hasParentCapability: false).Allowed)
            .ToArray();

    private static BrokerCaller SampleChild => new()
    {
        Class = BrokerCallerClass.ChildSession,
        Sid = "S-1-5-21-0-0-0-1001",
        AccountName = "child"
    };
}
