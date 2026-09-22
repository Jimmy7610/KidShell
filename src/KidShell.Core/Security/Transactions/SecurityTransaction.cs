using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Security.Transactions;

public enum TransactionState
{
    /// <summary>Built but not started.</summary>
    Created = 0,

    /// <summary>Running preflight on every operation.</summary>
    Preflight = 1,

    /// <summary>Capturing previous state.</summary>
    Snapshotting = 2,

    /// <summary>Applying changes.</summary>
    Applying = 3,

    /// <summary>Reading changes back.</summary>
    Verifying = 4,

    /// <summary>Everything applied and verified.</summary>
    Committed = 5,

    /// <summary>Something failed and every applied change was undone.</summary>
    RolledBack = 6,

    /// <summary>
    /// Something failed AND the rollback failed. The worst outcome, and the
    /// one the recovery manifest exists for: the machine is in a state nobody
    /// intended and a human has to be told exactly what happened.
    /// </summary>
    RollbackFailed = 7,

    /// <summary>Refused before anything was touched.</summary>
    Refused = 8
}

/// <summary>What happened, in full.</summary>
public sealed record TransactionResult
{
    public required string TransactionId { get; init; }

    public required TransactionState State { get; init; }

    public required SecurityExecutionMode ExecutionMode { get; init; }

    public IReadOnlyList<string> AppliedOperations { get; init; } = [];

    public IReadOnlyList<string> RolledBackOperations { get; init; } = [];

    /// <summary>Parent-facing reason, Swedish, when the transaction did not commit.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>The operation that failed, if any.</summary>
    public string? FailedOperationId { get; init; }

    public IReadOnlyList<OperationSnapshot> Snapshots { get; init; } = [];

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }

    public bool Succeeded => State == TransactionState.Committed;

    /// <summary>
    /// True when the machine may be in a state nobody chose. The only
    /// condition that warrants telling a parent to seek help.
    /// </summary>
    public bool NeedsManualRecovery => State == TransactionState.RollbackFailed;
}

/// <summary>
/// Runs a set of <see cref="ISecurityOperation"/> as one unit.
///
///     Preflight all -> Snapshot all -> Apply each -> Verify each
///     any failure -> Rollback everything already applied, newest first
///
/// Three rules make this safe rather than merely orderly:
///
///  1. **Everything is preflighted before anything is applied.** A transaction
///     that would fail on step 7 must not have performed steps 1-6.
///  2. **Every state is captured before any change.** Snapshotting during
///     application would mean an early failure leaves later operations with
///     nothing to restore.
///  3. **An operation that cannot roll back cannot take part.** Admitting one
///     would make the whole transaction irreversible, which defeats the point.
///
/// In this milestone the coordinator only ever receives an AuditOnly context,
/// so Apply refuses and the transaction reports Refused without touching
/// anything.
/// </summary>
public sealed class SecurityTransaction
{
    private readonly IReadOnlyList<ISecurityOperation> _operations;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;

    public SecurityTransaction(
        IEnumerable<ISecurityOperation> operations,
        IKidShellLogger logger,
        TimeProvider? time = null)
    {
        _operations = [.. operations];
        _logger = logger;
        _time = time ?? TimeProvider.System;
        TransactionId = Guid.NewGuid().ToString("n")[..12];
    }

    public string TransactionId { get; }

    public TransactionState State { get; private set; } = TransactionState.Created;

    public IReadOnlyList<ISecurityOperation> Operations => _operations;

    public async Task<TransactionResult> ExecuteAsync(
        SecurityExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var startedAt = _time.GetUtcNow();
        var snapshots = new List<OperationSnapshot>();
        var applied = new List<ISecurityOperation>();

        // ---------------------------------------------------------- refuse
        // The whole point of the arming design: an AuditOnly context cannot
        // change anything, and says so before doing any work at all.
        if (context.Mode != SecurityExecutionMode.Apply)
        {
            _logger.Info(SecurityAuditEvents.Category,
                $"Transaction {TransactionId} refused: execution mode is {context.Mode}. Nothing was changed.");

            return Finish(context.Mode, TransactionState.Refused, startedAt, snapshots, applied,
                "KidShell körs i granskningsläge och ändrar ingenting.", null);
        }

        // ------------------------------------------------- irreversible check
        var irreversible = _operations.Where(o => !o.CanRollback).ToList();

        if (irreversible.Count > 0)
        {
            _logger.Error(SecurityAuditEvents.Category,
                $"Transaction {TransactionId} refused: {irreversible.Count} operation(s) cannot roll back.");

            return Finish(context.Mode, TransactionState.Refused, startedAt, snapshots, applied,
                "Ett av stegen kan inte ångras, så ingenting utfördes.",
                irreversible[0].Id);
        }

        // ---------------------------------------------------------- preflight
        State = TransactionState.Preflight;

        foreach (var operation in _operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await Safely(
                () => operation.PreflightAsync(context, cancellationToken),
                $"Preflight för {operation.Id} kunde inte köras.").ConfigureAwait(false);

            if (!outcome.Success)
            {
                _logger.Warning(SecurityAuditEvents.Category,
                    $"Transaction {TransactionId} refused at preflight of {operation.Id}: {outcome.Detail ?? outcome.Message}");

                return Finish(context.Mode, TransactionState.Refused, startedAt, snapshots, applied,
                    outcome.Message, operation.Id);
            }
        }

        // --------------------------------------------------------- snapshot
        State = TransactionState.Snapshotting;

        foreach (var operation in _operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                snapshots.Add(await operation.CaptureStateAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // A state that cannot be captured cannot be restored, so the
                // transaction stops here rather than becoming irreversible.
                _logger.Error(SecurityAuditEvents.Category,
                    $"Transaction {TransactionId} refused: could not capture state for {operation.Id}.", ex);

                return Finish(context.Mode, TransactionState.Refused, startedAt, snapshots, applied,
                    "Nuvarande inställningar kunde inte sparas undan, så ingenting ändrades.",
                    operation.Id);
            }
        }

        // ------------------------------------------------------ apply/verify
        foreach (var operation in _operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            State = TransactionState.Applying;

            var applyOutcome = await Safely(
                () => operation.ApplyAsync(context, cancellationToken),
                $"{operation.Description} kunde inte genomföras.").ConfigureAwait(false);

            if (!applyOutcome.Success)
            {
                return await RollbackAsync(
                    context, startedAt, snapshots, applied, operation.Id, applyOutcome.Message, cancellationToken)
                    .ConfigureAwait(false);
            }

            applied.Add(operation);

            State = TransactionState.Verifying;

            // Read it back. Windows can accept a write and not honour it, so
            // a successful Apply is a claim, not evidence.
            var verifyOutcome = await Safely(
                () => operation.VerifyAsync(context, cancellationToken),
                $"{operation.Description} kunde inte bekräftas.").ConfigureAwait(false);

            if (!verifyOutcome.Success)
            {
                return await RollbackAsync(
                    context, startedAt, snapshots, applied, operation.Id, verifyOutcome.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.Info(SecurityAuditEvents.Category,
            $"Transaction {TransactionId} committed: {applied.Count} operation(s) applied and verified.");

        return Finish(context.Mode, TransactionState.Committed, startedAt, snapshots, applied, null, null);
    }

    /// <summary>
    /// Undoes everything already applied, newest first, and keeps going even
    /// when one rollback fails - stopping early would strand more state than
    /// continuing does.
    /// </summary>
    private async Task<TransactionResult> RollbackAsync(
        SecurityExecutionContext context,
        DateTimeOffset startedAt,
        List<OperationSnapshot> snapshots,
        List<ISecurityOperation> applied,
        string failedOperationId,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        _logger.Warning(SecurityAuditEvents.Category,
            $"Transaction {TransactionId} failed at {failedOperationId}; rolling back {applied.Count} operation(s).");

        var rolledBack = new List<string>();
        var rollbackFailed = false;

        for (var i = applied.Count - 1; i >= 0; i--)
        {
            var operation = applied[i];
            var snapshot = snapshots.FirstOrDefault(s => s.OperationId == operation.Id);

            if (snapshot is null)
            {
                rollbackFailed = true;
                _logger.Error(SecurityAuditEvents.Category,
                    $"Transaction {TransactionId}: no snapshot for {operation.Id}; cannot roll it back.");
                continue;
            }

            var outcome = await Safely(
                () => operation.RollbackAsync(snapshot, context, cancellationToken),
                $"{operation.Description} kunde inte återställas.").ConfigureAwait(false);

            if (outcome.Success)
            {
                rolledBack.Add(operation.Id);
            }
            else
            {
                rollbackFailed = true;
                _logger.Error(SecurityAuditEvents.Category,
                    $"Transaction {TransactionId}: rollback of {operation.Id} FAILED: {outcome.Detail ?? outcome.Message}");
            }
        }

        var state = rollbackFailed ? TransactionState.RollbackFailed : TransactionState.RolledBack;

        var message = rollbackFailed
            ? failureMessage + " Vissa ändringar kunde inte återställas automatiskt."
            : failureMessage + " Allt återställdes.";

        return Finish(context.Mode, state, startedAt, snapshots, applied, message, failedOperationId, rolledBack);
    }

    /// <summary>
    /// Wraps a stage so an exception becomes a failed outcome. A throwing
    /// operation must trigger rollback, not escape and leave the transaction
    /// half-applied with nobody handling it.
    /// </summary>
    private async Task<OperationOutcome> Safely(Func<Task<OperationOutcome>> stage, string friendlyMessage)
    {
        try
        {
            return await stage().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(SecurityAuditEvents.Category, $"Transaction {TransactionId}: stage threw.", ex);
            return OperationOutcome.Fail(friendlyMessage, ex.ToString());
        }
    }

    private TransactionResult Finish(
        SecurityExecutionMode mode,
        TransactionState state,
        DateTimeOffset startedAt,
        List<OperationSnapshot> snapshots,
        List<ISecurityOperation> applied,
        string? failureMessage,
        string? failedOperationId,
        List<string>? rolledBack = null)
    {
        State = state;

        return new TransactionResult
        {
            TransactionId = TransactionId,
            State = state,
            ExecutionMode = mode,
            AppliedOperations = [.. applied.Select(o => o.Id)],
            RolledBackOperations = rolledBack ?? [],
            FailureMessage = failureMessage,
            FailedOperationId = failedOperationId,
            Snapshots = snapshots,
            StartedAtUtc = startedAt,
            CompletedAtUtc = _time.GetUtcNow()
        };
    }
}
