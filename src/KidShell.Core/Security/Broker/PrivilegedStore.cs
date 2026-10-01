using KidShell.Core.Security.Storage;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// The privileged side's view of the protected store: read, write, and one
/// staging slot.
///
/// WHY READ IS HERE AND NOT ONLY ON THE CHILD'S SIDE
/// -------------------------------------------------
/// Because the service has to know what it already holds. Every monotonic
/// rule in <see cref="ProtectedStateTransitionRules"/> compares a proposal
/// against the current document, and a service that cannot read cannot
/// compare - it can only write what it is told, which is the thing this pass
/// exists to stop.
///
/// There is still no member that takes a path. The document is named by a
/// closed enum and the implementation owns the mapping, exactly as before.
/// </summary>
public interface IPrivilegedProtectedStore
{
    /// <summary>The document as it is on disk, or null when there is none.</summary>
    string? Read(ProtectedDocument document);

    ProtectedWriteResult Write(ProtectedDocument document, string json);

    /// <summary>
    /// The policy a child's session has proposed, awaiting approval.
    ///
    /// A separate slot, and nothing reads it for enforcement. That is what
    /// makes letting the child write it safe: a proposal that is never
    /// consulted is not a policy.
    /// </summary>
    string? ReadStaged();

    ProtectedWriteResult WriteStaged(string json);

    /// <summary>Discards the proposal, once it has been approved or refused.</summary>
    ProtectedWriteResult ClearStaged();
}
