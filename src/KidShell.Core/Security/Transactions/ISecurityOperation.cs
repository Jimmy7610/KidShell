using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Security.Transactions;

/// <summary>Outcome of one stage of an operation.</summary>
public sealed record OperationOutcome
{
    public required bool Success { get; init; }

    /// <summary>Plain-language, parent-facing. Swedish. Never an HRESULT.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Technical detail for the log. Never shown to a parent.</summary>
    public string? Detail { get; init; }

    public static OperationOutcome Ok(string message = "") => new() { Success = true, Message = message };

    public static OperationOutcome Fail(string message, string? detail = null) =>
        new() { Success = false, Message = message, Detail = detail };
}

/// <summary>
/// A captured "before" value, so a change can be undone.
///
/// Snapshots are opaque strings on purpose: an operation knows how to read its
/// own state back, and the coordinator only has to store and return it. That
/// keeps the recovery manifest serialisable without the coordinator
/// understanding registry values, account flags or policy XML.
/// </summary>
public sealed record OperationSnapshot
{
    public required string OperationId { get; init; }

    /// <summary>What was captured, for the manifest and for a human reading it.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The operation's own serialised representation of the previous state.
    /// Null means "there was nothing here", which is itself a state worth
    /// recording - undoing a creation means deleting.
    /// </summary>
    public string? PreviousValue { get; init; }

    /// <summary>Whether the state existed before the operation ran.</summary>
    public bool ExistedBefore { get; init; }

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One reversible change to Windows.
///
/// The lifecycle is fixed and total:
///
///     Preflight -> CaptureState -> Apply -> Verify -> (Commit | Rollback)
///
/// Every stage may refuse. An operation that cannot capture its previous state
/// must fail preflight rather than proceed, because a change that cannot be
/// described cannot be undone - and a parent locked out of their own machine
/// is the one outcome this whole design exists to prevent.
///
/// IMPORTANT: no implementation of this interface exists in KidShell, and a
/// test asserts that. The interface is the shape the future work has to fit,
/// and writing the first implementation is a visible, reviewable act rather
/// than something that can happen by accident.
/// </summary>
public interface ISecurityOperation
{
    string Id { get; }

    /// <summary>Parent-facing description, Swedish.</summary>
    string Description { get; }

    ChangeRiskLevel RiskLevel { get; }

    bool RequiresAdministrator { get; }

    /// <summary>
    /// Whether this operation can undo itself. An operation that answers false
    /// may not be included in a transaction at all - see
    /// <see cref="SecurityTransaction"/>.
    /// </summary>
    bool CanRollback { get; }

    /// <summary>Which capability the machine must have for this to be possible.</summary>
    RequiredCapability CapabilityRequired { get; }

    /// <summary>Can this run here, right now? Reads only.</summary>
    Task<OperationOutcome> PreflightAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>Records the current state so it can be restored. Reads only.</summary>
    Task<OperationSnapshot> CaptureStateAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the change. MUST throw unless the context is in Apply mode, which
    /// currently cannot be constructed.
    /// </summary>
    Task<OperationOutcome> ApplyAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the change actually took effect, by reading it back rather
    /// than by trusting that Apply returned success. Windows can accept a
    /// write and not honour it.
    /// </summary>
    Task<OperationOutcome> VerifyAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default);

    /// <summary>Restores the captured state.</summary>
    Task<OperationOutcome> RollbackAsync(
        OperationSnapshot snapshot,
        SecurityExecutionContext context,
        CancellationToken cancellationToken = default);
}
