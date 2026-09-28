using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// EXTERNAL AUDIT FINDING 07 — a failed Apply left its own changes behind.
///
/// THE BUG
/// -------
/// The coordinator added an operation to the rollback list only after Apply
/// had SUCCEEDED, and separately when Apply was cancelled. A plain failure
/// result took neither path, so the one operation most likely to have left
/// half of its work on the machine was the one operation never rolled back:
///
///     apply step 1 of 3   -> done
///     apply step 2 of 3   -> fails
///     return Fail(...)    -> not in the applied list
///     rollback            -> undoes the EARLIER operations, not this one
///
/// An operation is rarely one atomic write, so "Apply returned failed" never
/// meant "Apply changed nothing". A thrown exception took the same path.
///
/// THE CONTRACT NOW
/// ----------------
/// Once Apply BEGINS, the operation is a rollback candidate. It stays one
/// whatever happens next - success, failure, exception, cancellation, or a
/// verification that does not agree with it - until the transaction commits.
///
/// That deliberately rolls back operations which may have changed nothing.
/// Rollback restores a captured previous value, so undoing something that
/// never happened writes back what was already there; leaving a partial
/// mutation on a family's computer is the failure that matters.
/// </summary>
public class SecurityPartialApplyTests
{
    private static SecurityTransaction Build(RecordingLogger logger, params ISecurityOperation[] operations) =>
        new(operations, new RecordingManifestStore([]), TestMachineSummary.Create(), logger);

    /// <summary>The bug, stated as directly as it can be.</summary>
    [Fact]
    public async Task An_operation_that_changes_something_and_then_fails_is_rolled_back()
    {
        var logger = new RecordingLogger();
        var failing = new FakeOperation("halfway", applyOk: false) { MutateBeforeFailing = true };

        var result = await Build(logger, failing).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, failing.RollbackCount);
        Assert.False(failing.IsMutated, "the half-applied change was left on the machine");
    }

    [Fact]
    public async Task An_operation_that_changes_something_and_then_throws_is_rolled_back()
    {
        var logger = new RecordingLogger();
        var exploding = new FakeOperation("boom", throwOnApply: true) { MutateBeforeFailing = true };

        var result = await Build(logger, exploding).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, exploding.RollbackCount);
        Assert.False(exploding.IsMutated);
    }

    [Fact]
    public async Task An_operation_that_fails_without_changing_anything_is_still_rolled_back()
    {
        // Deliberate. The coordinator cannot tell the difference between "I
        // failed before touching anything" and "I failed halfway", and an
        // operation's own claim about that is exactly what must not be trusted.
        // Restoring a value that was never changed is a no-op; the reverse
        // mistake is not.
        var logger = new RecordingLogger();
        var failing = new FakeOperation("clean-failure", applyOk: false);

        var result = await Build(logger, failing).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, failing.RollbackCount);
    }

    [Fact]
    public async Task A_verification_failure_rolls_back_the_operation_it_could_not_confirm()
    {
        var logger = new RecordingLogger();
        var unverifiable = new FakeOperation("unverified", verifyOk: false) { MutateBeforeFailing = true };

        var result = await Build(logger, unverifiable).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, unverifiable.RollbackCount);
        Assert.False(unverifiable.IsMutated);
    }

    [Fact]
    public async Task A_cancelled_apply_is_rolled_back()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        var cancelling = new FakeOperation("cancelled") { CancelDuringApply = cts };

        var result = await Build(logger, cancelling).ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, cancelling.RollbackCount);
    }

    /// <summary>
    /// The failing operation is undone before the ones that came before it,
    /// because it is the most recent change on the machine.
    /// </summary>
    [Fact]
    public async Task Rollback_starts_with_the_operation_that_failed()
    {
        var journal = new List<string>();
        var logger = new RecordingLogger();

        var first = new FakeOperation("first") { Journal = journal };
        var second = new FakeOperation("second") { Journal = journal };
        var third = new FakeOperation("third", applyOk: false) { Journal = journal, MutateBeforeFailing = true };

        var result = await new SecurityTransaction(
            [first, second, third],
            new RecordingManifestStore(journal),
            TestMachineSummary.Create(),
            logger).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RolledBack, result.State);

        var rollbacks = journal.Where(e => e.StartsWith("rollback:", StringComparison.Ordinal)).ToList();

        Assert.Equal(["rollback:third", "rollback:second", "rollback:first"], rollbacks);
        Assert.False(third.IsMutated);
    }

    [Fact]
    public async Task Every_earlier_operation_is_rolled_back_when_a_later_one_fails()
    {
        var logger = new RecordingLogger();

        var first = new FakeOperation("first");
        var second = new FakeOperation("second", applyOk: false) { MutateBeforeFailing = true };

        var result = await new SecurityTransaction(
            [first, second],
            new RecordingManifestStore([]),
            TestMachineSummary.Create(),
            logger).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RolledBack, result.State);
        Assert.Equal(1, first.RollbackCount);
        Assert.Equal(1, second.RollbackCount);
        Assert.Equal(["second", "first"], result.RolledBackOperations);
    }

    /// <summary>
    /// When the failing operation cannot be undone either, the transaction
    /// says so rather than reporting a tidy recovery.
    /// </summary>
    [Fact]
    public async Task A_failure_to_roll_back_the_failing_operation_asks_for_human_help()
    {
        var logger = new RecordingLogger();
        var stuck = new FakeOperation("stuck", applyOk: false, rollbackOk: false) { MutateBeforeFailing = true };

        var result = await Build(logger, stuck).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RollbackFailed, result.State);
        Assert.True(result.NeedsManualRecovery);
        Assert.True(stuck.IsMutated, "the fake only clears this on a successful rollback");
    }

    [Fact]
    public async Task Several_rollback_failures_are_all_attempted()
    {
        var logger = new RecordingLogger();

        var first = new FakeOperation("first", rollbackOk: false);
        var second = new FakeOperation("second", applyOk: false, rollbackOk: false);

        var result = await new SecurityTransaction(
            [first, second],
            new RecordingManifestStore([]),
            TestMachineSummary.Create(),
            logger).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.RollbackFailed, result.State);

        // Both were tried. Stopping at the first failure would strand more
        // state than carrying on does.
        Assert.Equal(1, first.RollbackCount);
        Assert.Equal(1, second.RollbackCount);
        Assert.Empty(result.RolledBackOperations);
    }

    /// <summary>
    /// A failed operation is a rollback candidate, not an applied one. The
    /// report has to keep those apart or it would tell a parent that something
    /// succeeded when it did not.
    /// </summary>
    [Fact]
    public async Task A_failed_operation_is_not_reported_as_applied()
    {
        var logger = new RecordingLogger();

        var succeeded = new FakeOperation("ok");
        var failed = new FakeOperation("bad", applyOk: false);

        var result = await new SecurityTransaction(
            [succeeded, failed],
            new RecordingManifestStore([]),
            TestMachineSummary.Create(),
            logger).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(["ok"], result.AppliedOperations);
        Assert.Equal("bad", result.FailedOperationId);

        // But it was still undone.
        Assert.Contains("bad", result.RolledBackOperations);
    }

    /// <summary>
    /// Nothing changes for the happy path: a transaction that commits rolls
    /// nothing back.
    /// </summary>
    [Fact]
    public async Task A_committed_transaction_rolls_nothing_back()
    {
        var logger = new RecordingLogger();
        var first = new FakeOperation("first");
        var second = new FakeOperation("second");

        var result = await new SecurityTransaction(
            [first, second],
            new RecordingManifestStore([]),
            TestMachineSummary.Create(),
            logger).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.Committed, result.State);
        Assert.Equal(0, first.RollbackCount);
        Assert.Equal(0, second.RollbackCount);
        Assert.Equal(["first", "second"], result.AppliedOperations);
    }

    /// <summary>
    /// Cancellation before the first Apply is still a clean abort. Making
    /// failures roll back must not turn "nothing happened" into a rollback of
    /// operations that never ran.
    /// </summary>
    [Fact]
    public async Task Cancelling_before_the_first_apply_still_changes_nothing()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        var operation = new FakeOperation("a") { CancelAfterSnapshot = cts };

        var result = await Build(logger, operation).ExecuteAsync(ApplyContextForTests.Create(), cts.Token);

        Assert.Equal(TransactionState.Cancelled, result.State);
        Assert.Equal(0, operation.ApplyCount);
        Assert.Equal(0, operation.RollbackCount);
    }
}
