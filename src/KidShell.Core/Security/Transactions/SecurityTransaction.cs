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
    Refused = 8,

    /// <summary>
    /// The caller cancelled before anything had been applied. Distinct from
    /// <see cref="Refused"/>: refused means KidShell declined, cancelled means
    /// somebody asked it to stop. Either way nothing was changed.
    ///
    /// Cancellation AFTER something has been applied never reaches this state -
    /// it rolls back, and reports <see cref="RolledBack"/> or
    /// <see cref="RollbackFailed"/> like any other failure.
    /// </summary>
    Cancelled = 9
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

    /// <summary>
    /// Whether a cancellation request ended this transaction. Rollback may
    /// still have run - check <see cref="State"/> for what actually happened.
    /// </summary>
    public bool WasCancelled { get; init; }

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
/// Cancellation obeys the same principle, with a hinge at the first Apply:
///
///  * **before** anything is applied, cancellation is a clean abort - the
///    transaction reports <see cref="TransactionState.Cancelled"/> and nothing
///    was changed;
///  * **after** anything is applied, cancellation is a failure like any other
///    and rolls everything back, newest first.
///
/// Rollback therefore never observes the caller's token. Being asked to stop
/// is the *reason* rollback is running, so a cancelled token must not also
/// cancel the recovery - that would turn "stop" into "stop halfway".
///
/// In this milestone the coordinator only ever receives an AuditOnly context,
/// so Apply refuses and the transaction reports Refused without touching
/// anything.
/// </summary>
public sealed class SecurityTransaction
{
    /// <summary>
    /// How long rollback is allowed to take in total.
    ///
    /// Rollback ignores the caller's cancellation token, so it still needs
    /// some ceiling or a hung operation would block forever. This budget is
    /// the only thing that can stop a rollback early.
    /// </summary>
    public static readonly TimeSpan RollbackBudget = TimeSpan.FromMinutes(2);

    private const string CancellationMessage = "Åtgärden avbröts.";

    /// <summary>
    /// The result of one stage, keeping "failed" and "cancelled" apart.
    ///
    /// That distinction is the whole reason this type exists: both must
    /// trigger rollback once something has been applied, but only cancellation
    /// before that point is a clean abort rather than a failure.
    /// </summary>
    private sealed record StageResult(bool Success, bool Cancelled, string Message, string? Detail)
    {
        public static StageResult From(OperationOutcome outcome) =>
            new(outcome.Success, false, outcome.Message, outcome.Detail);

        public static StageResult FromCancellation() =>
            new(false, true, CancellationMessage, nameof(OperationCanceledException));

        public static StageResult Threw(string message, string detail) =>
            new(false, false, message, detail);
    }

    private readonly IReadOnlyList<ISecurityOperation> _operations;
    private readonly IRecoveryManifestStore _recovery;
    private readonly RecoveryMachineSummary _machine;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Whether this transaction's manifest is on disk. Set only by a
    /// successful write, and the gate for completing it afterwards.
    /// </summary>
    private bool _manifestWritten;

    /// <summary>
    /// The recovery store is a required dependency, not an option.
    ///
    /// That is the whole of the ordering guarantee: a transaction cannot be
    /// built without somewhere to write its manifest, and
    /// <see cref="ExecuteAsync"/> is the only way to run one. There is no
    /// overload, no null default and no second entry point, so "apply first,
    /// write the manifest afterwards" is not an expressible call sequence.
    /// </summary>
    public SecurityTransaction(
        IEnumerable<ISecurityOperation> operations,
        IRecoveryManifestStore recovery,
        RecoveryMachineSummary machine,
        IKidShellLogger logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(logger);

        _operations = [.. operations];
        _recovery = recovery;
        _machine = machine;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        TransactionId = Guid.NewGuid().ToString("n")[..12];
    }

    public string TransactionId { get; }

    public TransactionState State { get; private set; } = TransactionState.Created;

    public IReadOnlyList<ISecurityOperation> Operations => _operations;

    /// <summary>
    /// Runs the transaction. The only entry point, deliberately.
    /// </summary>
    public async Task<TransactionResult> ExecuteAsync(
        SecurityExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await RunAsync(context, cancellationToken).ConfigureAwait(false);

        // Stamp the outcome onto the manifest, if one was written. Best
        // effort: a manifest that cannot be updated still says "unfinished",
        // which errs towards asking a human to look rather than away from it.
        if (_manifestWritten)
        {
            await CompleteManifestAsync(result.State).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<TransactionResult> RunAsync(
        SecurityExecutionContext context,
        CancellationToken cancellationToken)
    {
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
            // Nothing has been applied yet, so stopping here changes nothing.
            if (cancellationToken.IsCancellationRequested)
            {
                return CleanAbort(context.Mode, startedAt, snapshots, applied);
            }

            var stage = await Safely(
                () => operation.PreflightAsync(context, cancellationToken),
                $"Preflight för {operation.Id} kunde inte köras.").ConfigureAwait(false);

            if (stage.Cancelled)
            {
                return CleanAbort(context.Mode, startedAt, snapshots, applied);
            }

            if (!stage.Success)
            {
                _logger.Warning(SecurityAuditEvents.Category,
                    $"Transaction {TransactionId} refused at preflight of {operation.Id}: {stage.Detail ?? stage.Message}");

                return Finish(context.Mode, TransactionState.Refused, startedAt, snapshots, applied,
                    stage.Message, operation.Id);
            }
        }

        // --------------------------------------------------------- snapshot
        State = TransactionState.Snapshotting;

        foreach (var operation in _operations)
        {
            // Still nothing applied; also a clean abort.
            if (cancellationToken.IsCancellationRequested)
            {
                return CleanAbort(context.Mode, startedAt, snapshots, applied);
            }

            try
            {
                snapshots.Add(await operation.CaptureStateAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                return CleanAbort(context.Mode, startedAt, snapshots, applied);
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

        // -------------------------------------------------- recovery manifest
        // The ordering rule, enforced structurally by being right here:
        //
        //     preflight all -> capture all state -> WRITE THE MANIFEST ->
        //     only then apply anything
        //
        // It is written from the snapshots, so it can only be built once every
        // previous value is in hand, and the write must SUCCEED. A machine
        // changed without a readable record of how to change it back is the
        // failure this whole design exists to prevent, so "the manifest could
        // not be saved" refuses the transaction rather than proceeding
        // hopefully.
        if (cancellationToken.IsCancellationRequested)
        {
            return CleanAbort(context.Mode, startedAt, snapshots, applied);
        }

        var manifestWritten = await WriteManifestAsync(
            context, startedAt, snapshots, cancellationToken).ConfigureAwait(false);

        if (manifestWritten is null)
        {
            return CleanAbort(context.Mode, startedAt, snapshots, applied);
        }

        if (!manifestWritten.Value)
        {
            _logger.Error(SecurityAuditEvents.Category,
                $"Transaction {TransactionId} refused: the recovery manifest could not be written. Nothing was applied.");

            return Finish(context.Mode, TransactionState.Refused, startedAt, snapshots, applied,
                "Återställningsfilen kunde inte sparas, så ingenting ändrades.", null);
        }

        _manifestWritten = true;

        // ------------------------------------------------------ apply/verify
        foreach (var operation in _operations)
        {
            // The hinge. Once anything has been applied, stopping means undoing
            // rather than walking away, so what cancellation *means* depends on
            // whether the applied list is empty.
            if (cancellationToken.IsCancellationRequested)
            {
                return applied.Count == 0
                    ? CleanAbort(context.Mode, startedAt, snapshots, applied)
                    : await RollbackAsync(
                        context, startedAt, snapshots, applied,
                        operation.Id, CancellationMessage, wasCancelled: true).ConfigureAwait(false);
            }

            State = TransactionState.Applying;

            var applyStage = await Safely(
                () => operation.ApplyAsync(context, cancellationToken),
                $"{operation.Description} kunde inte genomföras.").ConfigureAwait(false);

            if (applyStage.Cancelled)
            {
                // A cancelled Apply may or may not have taken effect before it
                // noticed, so it counts as applied and gets rolled back.
                // Undoing something that never happened is harmless; leaving
                // something applied is not.
                applied.Add(operation);

                return await RollbackAsync(
                    context, startedAt, snapshots, applied,
                    operation.Id, CancellationMessage, wasCancelled: true).ConfigureAwait(false);
            }

            if (!applyStage.Success)
            {
                return await RollbackAsync(
                    context, startedAt, snapshots, applied,
                    operation.Id, applyStage.Message, wasCancelled: false).ConfigureAwait(false);
            }

            applied.Add(operation);

            State = TransactionState.Verifying;

            // Read it back. Windows can accept a write and not honour it, so
            // a successful Apply is a claim, not evidence.
            var verifyStage = await Safely(
                () => operation.VerifyAsync(context, cancellationToken),
                $"{operation.Description} kunde inte bekräftas.").ConfigureAwait(false);

            if (verifyStage.Cancelled)
            {
                return await RollbackAsync(
                    context, startedAt, snapshots, applied,
                    operation.Id, CancellationMessage, wasCancelled: true).ConfigureAwait(false);
            }

            if (!verifyStage.Success)
            {
                return await RollbackAsync(
                    context, startedAt, snapshots, applied,
                    operation.Id, verifyStage.Message, wasCancelled: false).ConfigureAwait(false);
            }
        }

        _logger.Info(SecurityAuditEvents.Category,
            $"Transaction {TransactionId} committed: {applied.Count} operation(s) applied and verified.");

        return Finish(context.Mode, TransactionState.Committed, startedAt, snapshots, applied, null, null);
    }

    /// <summary>
    /// Writes the recovery manifest. Returns true on success, false on a
    /// refusal-worthy failure, and null when the write was cancelled - which
    /// at this point is still a clean abort, because nothing has been applied.
    /// </summary>
    private async Task<bool?> WriteManifestAsync(
        SecurityExecutionContext context,
        DateTimeOffset startedAt,
        List<OperationSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var manifest = new RecoveryManifest
        {
            TransactionId = TransactionId,
            CreatedAtUtc = startedAt,
            Machine = _machine with { ExecutionMode = context.Mode.ToString() },
            Steps = [.. snapshots.Select(ToStep)]
        };

        try
        {
            return await _recovery.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A store that throws is a store that did not write.
            _logger.Error(SecurityAuditEvents.Category,
                $"Transaction {TransactionId}: the recovery manifest store threw.", ex);
            return false;
        }
    }

    private static RecoveryStep ToStep(OperationSnapshot snapshot) => new()
    {
        OperationId = snapshot.OperationId,
        Description = snapshot.Description,
        PreviousValue = snapshot.PreviousValue,
        ExistedBefore = snapshot.ExistedBefore,

        // Written for somebody following this by hand, from another account,
        // on a bad day - so it says what to do, not what happened.
        ManualRollbackHint = snapshot.ExistedBefore
            ? "Återställ värdet som står under previousValue."
            : "Detta fanns inte tidigare. Ta bort det som skapades."
    };

    private async Task CompleteManifestAsync(TransactionState finalState)
    {
        try
        {
            // Not the caller's token: the outcome must be recorded even when
            // the caller has stopped waiting, for the same reason rollback
            // ignores it.
            using var cts = new CancellationTokenSource(RollbackBudget);
            await _recovery.CompleteAsync(TransactionId, finalState, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category,
                $"Transaction {TransactionId}: could not stamp the outcome onto the recovery manifest.", ex);
        }
    }

    /// <summary>
    /// Undoes everything already applied, newest first, and keeps going even
    /// when one rollback fails - stopping early would strand more state than
    /// continuing does.
    ///
    /// Note what is NOT a parameter: the caller's cancellation token. Rollback
    /// runs on a fresh token of its own, because the most likely reason to be
    /// here is that the caller's token was cancelled, and honouring it would
    /// abandon the machine in exactly the half-applied state this method
    /// exists to prevent. The only limit is <see cref="RollbackBudget"/>.
    /// </summary>
    private async Task<TransactionResult> RollbackAsync(
        SecurityExecutionContext context,
        DateTimeOffset startedAt,
        List<OperationSnapshot> snapshots,
        List<ISecurityOperation> applied,
        string failedOperationId,
        string failureMessage,
        bool wasCancelled)
    {
        _logger.Warning(SecurityAuditEvents.Category,
            wasCancelled
                ? $"Transaction {TransactionId} cancelled at {failedOperationId} after applying {applied.Count} operation(s); rolling back."
                : $"Transaction {TransactionId} failed at {failedOperationId}; rolling back {applied.Count} operation(s).");

        // Deliberately not linked to the caller's token.
        using var rollbackCts = new CancellationTokenSource(RollbackBudget);
        var rollbackToken = rollbackCts.Token;

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

            var stage = await Safely(
                () => operation.RollbackAsync(snapshot, context, rollbackToken),
                $"{operation.Description} kunde inte återställas.").ConfigureAwait(false);

            if (stage.Success)
            {
                rolledBack.Add(operation.Id);
            }
            else
            {
                // A rollback that was itself cancelled means the budget ran
                // out. That is a failed rollback, not a tidy stop: the state
                // is unknown and a human has to be told.
                rollbackFailed = true;
                _logger.Error(SecurityAuditEvents.Category,
                    $"Transaction {TransactionId}: rollback of {operation.Id} FAILED: {stage.Detail ?? stage.Message}");
            }
        }

        var state = rollbackFailed ? TransactionState.RollbackFailed : TransactionState.RolledBack;

        var message = rollbackFailed
            ? failureMessage + " Vissa ändringar kunde inte återställas automatiskt."
            : failureMessage + " Allt återställdes.";

        return Finish(context.Mode, state, startedAt, snapshots, applied, message, failedOperationId,
            rolledBack, wasCancelled);
    }

    /// <summary>
    /// Ends the transaction without having applied anything. Used only where
    /// the applied list is provably empty, so there is nothing to undo.
    /// </summary>
    private TransactionResult CleanAbort(
        SecurityExecutionMode mode,
        DateTimeOffset startedAt,
        List<OperationSnapshot> snapshots,
        List<ISecurityOperation> applied)
    {
        _logger.Info(SecurityAuditEvents.Category,
            $"Transaction {TransactionId} cancelled before anything was applied. Nothing was changed.");

        return Finish(mode, TransactionState.Cancelled, startedAt, snapshots, applied,
            CancellationMessage + " Ingenting hann ändras.", null, wasCancelled: true);
    }

    /// <summary>
    /// Wraps a stage so an exception becomes a result rather than an escape. A
    /// throwing operation must trigger the caller's handling, not unwind out of
    /// <c>ExecuteAsync</c> and leave the transaction half-applied with nobody
    /// looking after it.
    ///
    /// That includes <see cref="OperationCanceledException"/>. It used to be
    /// rethrown, which is the conventional thing to do and was wrong here: it
    /// skipped rollback entirely, so cancelling midway through would have left
    /// every already-applied change in place with no record and no undo. It is
    /// now reported as a cancelled stage, and the caller decides whether that
    /// means a clean abort or a rollback.
    /// </summary>
    private async Task<StageResult> Safely(Func<Task<OperationOutcome>> stage, string friendlyMessage)
    {
        try
        {
            return StageResult.From(await stage().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            _logger.Warning(SecurityAuditEvents.Category, $"Transaction {TransactionId}: stage cancelled.");
            return StageResult.FromCancellation();
        }
        catch (Exception ex)
        {
            _logger.Error(SecurityAuditEvents.Category, $"Transaction {TransactionId}: stage threw.", ex);
            return StageResult.Threw(friendlyMessage, ex.ToString());
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
        List<string>? rolledBack = null,
        bool wasCancelled = false)
    {
        State = state;

        return new TransactionResult
        {
            WasCancelled = wasCancelled,
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
