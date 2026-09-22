using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// What cancelling a security transaction does.
///
/// THE PRINCIPLE
/// -------------
/// There is one hinge, and it is the first Apply:
///
///   * BEFORE anything is applied, cancellation is free. The transaction
///     aborts and nothing was changed.
///   * AFTER anything is applied, cancellation is a failure like any other and
///     must roll back every applied operation in reverse order.
///   * Rollback does not stop because the original token was cancelled. Being
///     asked to stop is the reason it is running.
///
/// The old code got the middle rule wrong: a cancelled stage rethrew out of
/// ExecuteAsync, so a cancellation partway through would have left every
/// already-applied change in place, with no rollback and no record. It never
/// showed up because no test could reach the apply path at all - see
/// <see cref="ApplyContextForTests"/>.
///
/// NOTHING HERE TOUCHES WINDOWS. The operations are fakes that increment
/// counters, and the manifest store is in memory.
/// </summary>
public class SecurityCancellationTests
{
    private static SecurityTransaction Build(
        RecordingLogger logger,
        IRecoveryManifestStore store,
        params ISecurityOperation[] operations) =>
        new(operations, store, TestMachineSummary.Create(), logger);

    // ------------------------------------ 1. cancel during preflight

    [Fact]
    public async Task Cancelling_during_preflight_applies_nothing()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal, CancelDuringPreflight = cts };
        var b = new FakeOperation("b") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.Cancelled, result.State);
        Assert.True(result.WasCancelled);
        Assert.False(result.Succeeded);
        Assert.False(result.NeedsManualRecovery);

        // Nothing read, nothing written, nothing to undo.
        Assert.Equal(0, a.ApplyCount);
        Assert.Equal(0, b.ApplyCount);
        Assert.Equal(0, a.RollbackCount);
        Assert.Empty(result.AppliedOperations);
        Assert.Empty(result.RolledBackOperations);

        // Not even a manifest: there is nothing to recover from.
        Assert.Empty(store.Written);
    }

    // ------------------------------------ 2. cancel during snapshot

    [Fact]
    public async Task Cancelling_during_the_snapshot_applies_nothing()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal, CancelDuringSnapshot = cts };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.Cancelled, result.State);
        Assert.True(result.WasCancelled);

        // Preflight ran, capture was attempted, nothing was changed.
        Assert.Equal(1, a.PreflightCount);
        Assert.Equal(0, a.ApplyCount);
        Assert.Equal(0, a.RollbackCount);
        Assert.Empty(store.Written);
    }

    // ------------------------------------ 3. cancel before the first Apply

    [Fact]
    public async Task Cancelling_after_the_snapshot_but_before_the_first_apply_changes_nothing()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        // The last safe moment: every previous value is captured and nothing
        // has been touched.
        var a = new FakeOperation("a") { Journal = journal, CancelAfterSnapshot = cts };
        var b = new FakeOperation("b") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.Cancelled, result.State);
        Assert.Equal(0, a.ApplyCount);
        Assert.Equal(0, b.ApplyCount);
        Assert.Equal(0, a.RollbackCount);
        Assert.DoesNotContain("apply:a", journal);
    }

    [Fact]
    public async Task Cancelling_between_the_manifest_and_the_first_apply_changes_nothing()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();

        // Cancellation lands in the narrowest window there is: the manifest is
        // on disk, and not one change has been made.
        var store = new RecordingManifestStore(journal) { CancelAfterWrite = cts };
        var a = new FakeOperation("a") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.Cancelled, result.State);
        Assert.Equal(0, a.ApplyCount);
        Assert.Equal(0, a.RollbackCount);
    }

    // ------------------------------------ 4. cancel after the first Apply

    [Fact]
    public async Task Cancelling_after_the_first_apply_rolls_that_operation_back()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        // a really applies, and only then is the transaction cancelled.
        var a = new FakeOperation("a") { Journal = journal, CancelAfterApply = cts };
        var b = new FakeOperation("b") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        // The old behaviour was to throw out of ExecuteAsync here, leaving a
        // applied and nobody responsible for it.
        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.True(result.WasCancelled);
        Assert.False(result.Succeeded);

        Assert.Equal(1, a.RollbackCount);
        Assert.Equal("a", Assert.Single(result.RolledBackOperations));

        // b never started.
        Assert.Equal(0, b.ApplyCount);
        Assert.Equal(0, b.RollbackCount);
    }

    // ------------------------------------ 5. cancel during Verify

    [Fact]
    public async Task Cancelling_during_verification_rolls_the_applied_operation_back()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        // The change is on the machine; only the read-back was interrupted.
        // An unverified change is still a change.
        var a = new FakeOperation("a") { Journal = journal, CancelDuringVerify = cts };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.True(result.WasCancelled);
        Assert.Equal(1, a.ApplyCount);
        Assert.Equal(1, a.RollbackCount);
        Assert.Equal("a", Assert.Single(result.RolledBackOperations));
    }

    [Fact]
    public async Task An_apply_that_is_cancelled_midway_is_still_rolled_back()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        // Apply was interrupted, so whether it took effect is unknown. Undoing
        // something that never happened is harmless; leaving something applied
        // is not, so the unknown case is treated as applied.
        var a = new FakeOperation("a") { Journal = journal, CancelDuringApply = cts };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, a.RollbackCount);
    }

    // ------------------------------------ 6. reverse order

    [Fact]
    public async Task Cancelling_after_several_applies_rolls_back_in_reverse_order()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal };
        var b = new FakeOperation("b") { Journal = journal, CancelAfterApply = cts };
        var c = new FakeOperation("c") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b, c)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.RolledBack, result.State);

        // Newest first. Undoing in application order could restore a value
        // that a later operation depended on.
        Assert.Equal(["b", "a"], result.RolledBackOperations);

        var rollbacks = journal.Where(e => e.StartsWith("rollback:", StringComparison.Ordinal)).ToList();
        Assert.Equal(["rollback:b", "rollback:a"], rollbacks);

        // c was never reached, so it is not rolled back.
        Assert.Equal(0, c.ApplyCount);
        Assert.Equal(0, c.RollbackCount);
    }

    // ------------------------------------ 7. rollback ignores the dead token

    [Fact]
    public async Task Rollback_runs_even_though_the_original_token_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal };
        var b = new FakeOperation("b") { Journal = journal, CancelAfterApply = cts };
        var c = new FakeOperation("c") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b, c)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        // The caller's token is well and truly cancelled.
        Assert.True(cts.IsCancellationRequested);

        // And rollback still ran, on a token of its own. If the caller's token
        // were passed through, every RollbackAsync would have been handed an
        // already-cancelled token and recovery would stop halfway.
        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, a.RollbackCount);
        Assert.Equal(1, b.RollbackCount);
        Assert.False(a.RollbackSawCancelledToken);
        Assert.False(b.RollbackSawCancelledToken);
    }

    [Fact]
    public async Task A_transaction_cancelled_after_applying_never_throws_at_the_caller()
    {
        using var cts = new CancellationTokenSource();
        var store = new RecordingManifestStore([]);
        var a = new FakeOperation("a") { CancelAfterApply = cts };
        var b = new FakeOperation("b");

        // An OperationCanceledException escaping here is the original bug: the
        // caller gets an exception and no result, so nobody knows what was
        // applied or whether it was undone.
        var result = await Build(new RecordingLogger(), store, a, b)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.NotNull(result);
        Assert.NotNull(result.FailureMessage);
    }

    [Fact]
    public async Task A_cancellation_that_arrives_after_the_last_operation_succeeded_still_commits()
    {
        using var cts = new CancellationTokenSource();
        var store = new RecordingManifestStore([]);

        // The cancel lands after the final operation applied AND verified, so
        // there is no work left to stop. Rolling back a transaction that
        // wholly succeeded would undo a working configuration in order to
        // honour a request that arrived too late to matter - worse than
        // ignoring it.
        var a = new FakeOperation("a") { CancelAfterApply = cts };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.True(result.Succeeded);
        Assert.Equal(0, a.RollbackCount);
    }

    // ------------------------------------ 8. rollback failure during cancel

    [Fact]
    public async Task A_rollback_that_fails_during_cancellation_asks_for_a_human()
    {
        using var cts = new CancellationTokenSource();
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        // a applied and cannot undo itself. This is the one outcome that means
        // the machine is in a state nobody chose.
        var a = new FakeOperation("a", rollbackOk: false) { Journal = journal, CancelAfterApply = cts };
        var b = new FakeOperation("b") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b)
            .ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.RollbackFailed, result.State);
        Assert.True(result.NeedsManualRecovery);
        Assert.True(result.WasCancelled);
        Assert.False(result.Succeeded);

        // It was attempted, it just did not work.
        Assert.Equal(1, a.RollbackCount);
        Assert.DoesNotContain("a", result.RolledBackOperations);

        // And the manifest is stamped as needing attention, so it survives.
        Assert.Contains(TransactionState.RollbackFailed, store.Completed);
    }

    // ------------------------------------ 9. AuditOnly is unchanged

    [Fact]
    public async Task An_already_cancelled_token_does_not_change_audit_only_behaviour()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);
        var a = new FakeOperation("a") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(SecurityExecutionContext.AuditOnly(), cts.Token);

        // Refused, not Cancelled: the mode check comes first and is the
        // stronger statement. KidShell was never going to change anything.
        Assert.Equal(TransactionState.Refused, result.State);
        Assert.False(result.WasCancelled);
        Assert.Equal(SecurityExecutionMode.AuditOnly, result.ExecutionMode);

        Assert.Equal(0, a.PreflightCount);
        Assert.Equal(0, a.SnapshotCount);
        Assert.Equal(0, a.ApplyCount);
        Assert.Equal(0, a.RollbackCount);
        Assert.Empty(store.Written);
        Assert.Empty(journal);
    }

    [Fact]
    public async Task Cancellation_during_audit_only_still_writes_no_manifest()
    {
        using var cts = new CancellationTokenSource();
        var store = new RecordingManifestStore([]);
        var a = new FakeOperation("a") { CancelDuringPreflight = cts };

        var result = await Build(new RecordingLogger(), store, a)
            .ExecuteAsync(SecurityExecutionContext.AuditOnly(), cts.Token);

        Assert.Equal(TransactionState.Refused, result.State);
        Assert.Empty(store.Written);
        Assert.Empty(store.Completed);
    }

    // ------------------------------------ the happy path, for contrast

    [Fact]
    public async Task An_uncancelled_transaction_commits_and_rolls_nothing_back()
    {
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal };
        var b = new FakeOperation("b") { Journal = journal };

        var result = await Build(new RecordingLogger(), store, a, b)
            .ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.True(result.Succeeded);
        Assert.False(result.WasCancelled);
        Assert.Equal(["a", "b"], result.AppliedOperations);
        Assert.Empty(result.RolledBackOperations);
        Assert.Equal(0, a.RollbackCount);
        Assert.Equal(0, b.RollbackCount);
    }
}
