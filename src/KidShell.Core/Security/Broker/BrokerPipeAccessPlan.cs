namespace KidShell.Core.Security.Broker;

/// <summary>Who an entry in the pipe's access list is about.</summary>
public enum BrokerPipePrincipal
{
    /// <summary>LocalSystem. The service itself.</summary>
    System = 0,

    /// <summary>The local Administrators group. The parent, elevated.</summary>
    Administrators = 1,

    /// <summary>The child's own account, by SID.</summary>
    ChildAccount = 2,

    /// <summary>
    /// Everyone with an account on this machine.
    ///
    /// Present only in the fallback plan, for a device whose child account is
    /// not yet known. It is weaker, it is labelled, and the authorization
    /// matrix is what keeps it from mattering.
    /// </summary>
    AuthenticatedUsers = 3
}

/// <summary>What a principal may do with the pipe.</summary>
[Flags]
public enum BrokerPipeRights
{
    None = 0,

    /// <summary>Open the pipe and exchange messages. What a client needs.</summary>
    Connect = 1,

    /// <summary>
    /// Create another instance of this pipe name.
    ///
    /// The dangerous one, and the reason this is a separate flag rather than
    /// part of Connect. A principal that can create an instance of
    /// KidShell.Security.v1 can answer a client's requests - including
    /// "is this the right PIN?" - as though it were the service. No plan
    /// grants it to anyone but SYSTEM.
    /// </summary>
    CreateInstance = 2,

    /// <summary>Change who may do what.</summary>
    ChangePermissions = 4,

    FullControl = Connect | CreateInstance | ChangePermissions
}

/// <summary>One entry in the pipe's access list.</summary>
/// <param name="Principal">Who.</param>
/// <param name="Rights">What.</param>
/// <param name="Sid">
/// The specific SID, when the principal is an account rather than a
/// well-known group.
/// </param>
public sealed record BrokerPipeAce(BrokerPipePrincipal Principal, BrokerPipeRights Rights, string Sid = "");

/// <summary>
/// Who may talk to the privileged broker, as a plan rather than an action.
///
/// WHY AN ACL IS NOT THE AUTHORIZATION
/// -----------------------------------
/// The access list decides who may OPEN the pipe. It cannot decide what they
/// may ask for, because every caller that gets through it sends the same
/// kinds of message. So the two halves do different jobs and neither is
/// sufficient alone:
///
///   * the ACL keeps unrelated local accounts off the endpoint entirely, and
///     keeps everyone but SYSTEM from creating an instance of it;
///   * <see cref="BrokerAuthorizationPolicy"/> decides, per request, what the
///     caller it can now see is allowed to do.
///
/// An access list that said "Authenticated Users may write" and stopped there
/// would be the design this pass exists to avoid: a LocalSystem service that
/// writes the parent's policy for anybody who can open a file handle.
///
/// NOTHING HERE CHANGES A MACHINE. It produces a description that the Windows
/// listener turns into a real descriptor, and that tests can check on any
/// host.
/// </summary>
public sealed record BrokerPipeAccessPlan
{
    public required IReadOnlyList<BrokerPipeAce> Entries { get; init; }

    /// <summary>Whether the plan names the child specifically.</summary>
    public required bool IsChildScoped { get; init; }

    /// <summary>
    /// The plan for a provisioned device, where the child's SID is known.
    ///
    /// The narrowest thing Windows can express here. It is not proof that the
    /// caller is KidShell - a SID identifies an account, not a program, and
    /// binary identity needs code signing this product does not have yet. It
    /// is proof of WHICH ACCOUNT is asking, which is what the authorization
    /// matrix needs and what the old design never established at all.
    /// </summary>
    public static BrokerPipeAccessPlan ForChild(string childSid) => new()
    {
        IsChildScoped = true,
        Entries =
        [
            new(BrokerPipePrincipal.System, BrokerPipeRights.FullControl),
            new(BrokerPipePrincipal.Administrators,
                BrokerPipeRights.Connect | BrokerPipeRights.ChangePermissions),
            new(BrokerPipePrincipal.ChildAccount, BrokerPipeRights.Connect, childSid)

            // No entry for anyone else. Another family member's account has no
            // business asking this service anything, and silence is denial.
        ]
    };

    /// <summary>
    /// The plan for a device that has not been provisioned yet.
    ///
    /// Weaker on purpose and labelled as such: before a child account exists
    /// there is no SID to scope to, and a service that refused every caller
    /// could not be used to provision the machine. Every request from this
    /// plan still goes through the same authorization matrix, so what an
    /// unprivileged caller can actually DO is unchanged.
    /// </summary>
    public static BrokerPipeAccessPlan Unprovisioned() => new()
    {
        IsChildScoped = false,
        Entries =
        [
            new(BrokerPipePrincipal.System, BrokerPipeRights.FullControl),
            new(BrokerPipePrincipal.Administrators,
                BrokerPipeRights.Connect | BrokerPipeRights.ChangePermissions),
            new(BrokerPipePrincipal.AuthenticatedUsers, BrokerPipeRights.Connect)
        ]
    };

    /// <summary>
    /// Whether anybody but SYSTEM could stand up a pipe with this name.
    ///
    /// The property that stops the endpoint being impersonated. A principal
    /// holding CreateInstance can serve KidShell.Security.v1 itself and tell
    /// a client whatever it likes, which for VerifyParentPin means telling it
    /// the PIN was correct.
    /// </summary>
    public bool OnlySystemMayCreateInstances =>
        Entries.All(e => e.Principal == BrokerPipePrincipal.System
                         || !e.Rights.HasFlag(BrokerPipeRights.CreateInstance));

    /// <summary>Whether the child can reach the service at all.</summary>
    public bool IsReachableByChild =>
        Entries.Any(e => e.Principal is BrokerPipePrincipal.ChildAccount
                             or BrokerPipePrincipal.AuthenticatedUsers
                         && e.Rights.HasFlag(BrokerPipeRights.Connect));

    /// <summary>Whether an administrator can still reach it to repair things.</summary>
    public bool IsAdministratorRecoverable =>
        Entries.Any(e => e.Principal == BrokerPipePrincipal.Administrators
                         && e.Rights.HasFlag(BrokerPipeRights.Connect));
}
