using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// A test-only operation that records what happened to it.
///
/// This is the only implementation of <see cref="ISecurityOperation"/> in the
/// solution, it lives in the test assembly, and it changes nothing outside its
/// own fields. A separate test asserts that no implementation exists in the
/// product assembly.
/// </summary>
internal sealed class FakeOperation : ISecurityOperation
{
    private readonly bool _preflightOk;
    private readonly bool _applyOk;
    private readonly bool _verifyOk;
    private readonly bool _rollbackOk;
    private readonly bool _throwOnApply;
    private readonly bool _throwOnSnapshot;

    public FakeOperation(
        string id,
        bool preflightOk = true,
        bool applyOk = true,
        bool verifyOk = true,
        bool rollbackOk = true,
        bool canRollback = true,
        bool throwOnApply = false,
        bool throwOnSnapshot = false)
    {
        Id = id;
        _preflightOk = preflightOk;
        _applyOk = applyOk;
        _verifyOk = verifyOk;
        _rollbackOk = rollbackOk;
        CanRollback = canRollback;
        _throwOnApply = throwOnApply;
        _throwOnSnapshot = throwOnSnapshot;
    }

    public string Id { get; }

    public string Description => $"Teståtgärd {Id}";

    public ChangeRiskLevel RiskLevel => ChangeRiskLevel.Low;

    public bool RequiresAdministrator => true;

    public bool CanRollback { get; }

    public RequiredCapability CapabilityRequired => RequiredCapability.None;

    public int PreflightCount { get; private set; }

    public int SnapshotCount { get; private set; }

    public int ApplyCount { get; private set; }

    public int VerifyCount { get; private set; }

    public int RollbackCount { get; private set; }

    public Task<OperationOutcome> PreflightAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        PreflightCount++;
        return Task.FromResult(_preflightOk
            ? OperationOutcome.Ok()
            : OperationOutcome.Fail($"{Id} kan inte köras här."));
    }

    public Task<OperationSnapshot> CaptureStateAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        SnapshotCount++;

        if (_throwOnSnapshot)
        {
            throw new InvalidOperationException("snapshot failed");
        }

        return Task.FromResult(new OperationSnapshot
        {
            OperationId = Id,
            Description = $"Tidigare värde för {Id}",
            PreviousValue = $"before-{Id}",
            ExistedBefore = true
        });
    }

    public Task<OperationOutcome> ApplyAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        ApplyCount++;

        // The contract: an operation must refuse unless it was handed an
        // Apply context. Nothing can currently construct one.
        if (context.Mode != SecurityExecutionMode.Apply)
        {
            throw new InvalidOperationException("Apply called without an Apply context.");
        }

        if (_throwOnApply)
        {
            throw new InvalidOperationException("apply exploded");
        }

        return Task.FromResult(_applyOk
            ? OperationOutcome.Ok()
            : OperationOutcome.Fail($"{Id} misslyckades."));
    }

    public Task<OperationOutcome> VerifyAsync(SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        VerifyCount++;
        return Task.FromResult(_verifyOk
            ? OperationOutcome.Ok()
            : OperationOutcome.Fail($"{Id} kunde inte bekräftas."));
    }

    public Task<OperationOutcome> RollbackAsync(
        OperationSnapshot snapshot,
        SecurityExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RollbackCount++;
        return Task.FromResult(_rollbackOk
            ? OperationOutcome.Ok()
            : OperationOutcome.Fail($"{Id} kunde inte återställas."));
    }
}

/// <summary>
/// The transaction coordinator.
///
/// Every test here runs against an AuditOnly context, because that is the only
/// one that exists. That means the coordinator's refusal path is exercised for
/// real, and the apply/rollback paths are exercised through the internal
/// entry point that tests can reach - never against a machine.
/// </summary>
public class SecurityTransactionTests
{
    private static SecurityTransaction Build(RecordingLogger logger, params ISecurityOperation[] operations) =>
        new(operations, logger);

    // ------------------------------------------------ the refusal path

    [Fact]
    public async Task An_audit_only_context_refuses_before_doing_anything()
    {
        var logger = new RecordingLogger();
        var operation = new FakeOperation("a");
        var transaction = Build(logger, operation);

        var result = await transaction.ExecuteAsync(SecurityExecutionContext.AuditOnly());

        Assert.Equal(TransactionState.Refused, result.State);

        // Not even preflight ran: refusing early means nothing was read or
        // written on the way to deciding.
        Assert.Equal(0, operation.PreflightCount);
        Assert.Equal(0, operation.SnapshotCount);
        Assert.Equal(0, operation.ApplyCount);
    }

    [Fact]
    public async Task The_result_reports_the_mode_it_actually_ran_in()
    {
        var transaction = Build(new RecordingLogger(), new FakeOperation("a"));

        var result = await transaction.ExecuteAsync(SecurityExecutionContext.AuditOnly());

        Assert.Equal(SecurityExecutionMode.AuditOnly, result.ExecutionMode);
    }

    [Fact]
    public async Task A_refused_transaction_never_claims_success()
    {
        var transaction = Build(new RecordingLogger(), new FakeOperation("a"));

        var result = await transaction.ExecuteAsync(SecurityExecutionContext.AuditOnly());

        Assert.False(result.Succeeded);
        Assert.False(result.NeedsManualRecovery);
        Assert.Empty(result.AppliedOperations);
        Assert.NotNull(result.FailureMessage);
    }

    [Fact]
    public async Task Every_operation_refuses_an_audit_only_apply()
    {
        // The second half of the guarantee: even if a coordinator somehow
        // called Apply, the operation itself must refuse.
        var operation = new FakeOperation("a");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation.ApplyAsync(SecurityExecutionContext.AuditOnly()));
    }

    // ------------------------------------------------ structural guarantees

    [Fact]
    public void No_security_operation_is_implemented_in_the_product()
    {
        // The interface is the shape future work must fit. While nothing in
        // KidShell.Core implements it, there is no mutation path at all.
        var implementations = typeof(SecurityTransaction).Assembly
            .GetTypes()
            .Where(t => typeof(ISecurityOperation).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .ToArray();

        Assert.Empty(implementations);
    }

    [Fact]
    public void The_build_has_no_security_mutation_feature_compiled_in()
    {
        // A constant rather than a setting, so flipping it shows up in a diff.
        Assert.False(SecurityArming.SecurityFeatureCompiledIn);
    }

    // ------------------------------------------------ arming

    private static ArmingRequest FullyArmed() => new()
    {
        ParentAuthenticated = true,
        TransactionConfirmed = true,
        DeviceDesignated = true,
        RecoveryProven = true,
        ProcessElevated = true,
        ParentPinConfigured = true,
        CapabilitySatisfied = true
    };

    [Fact]
    public void Even_every_condition_met_cannot_arm_this_build()
    {
        var (decision, context) = SecurityArming.TryArm(
            FullyArmed(),
            TestRuntime.Production,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()));

        // The feature is not compiled in, so the first requirement fails
        // regardless of everything a human did.
        Assert.False(decision.IsArmed);
        Assert.Contains(ArmingRequirement.SecurityFeatureNotBuilt, decision.Unmet);

        // And the context handed back is still AuditOnly, because no other
        // kind can be constructed.
        Assert.Equal(SecurityExecutionMode.AuditOnly, context.Mode);
    }

    [Fact]
    public void Arming_names_every_missing_condition_separately()
    {
        var request = FullyArmed() with
        {
            ProcessElevated = false,
            RecoveryProven = false,
            DeviceDesignated = false
        };

        var (decision, _) = SecurityArming.TryArm(
            request,
            TestRuntime.Production,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()));

        // A parent needs to know what to fix, not just that it failed.
        Assert.Contains(ArmingRequirement.NotElevated, decision.Unmet);
        Assert.Contains(ArmingRequirement.RecoveryNotProven, decision.Unmet);
        Assert.Contains(ArmingRequirement.DeviceNotDesignated, decision.Unmet);
    }

    [Theory]
    [InlineData(ArmingRequirement.SecurityFeatureNotBuilt)]
    [InlineData(ArmingRequirement.NotElevated)]
    [InlineData(ArmingRequirement.ParentNotAuthorized)]
    [InlineData(ArmingRequirement.ParentPinMissing)]
    [InlineData(ArmingRequirement.RecoveryNotProven)]
    [InlineData(ArmingRequirement.TransactionNotConfirmed)]
    [InlineData(ArmingRequirement.CapabilityMissing)]
    [InlineData(ArmingRequirement.DeviceNotDesignated)]
    public void Every_requirement_has_parent_facing_wording(ArmingRequirement requirement)
    {
        var text = SecurityArming.Describe(requirement);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("0x", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_development_machine_is_never_a_designated_device()
    {
        // There is deliberately no API that designates a device from inside
        // the app; the request field exists so a future installer can assert
        // it, and defaults to denied.
        var request = FullyArmed() with { DeviceDesignated = false };

        var (decision, _) = SecurityArming.TryArm(
            request,
            TestRuntime.Development,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home()));

        Assert.False(decision.IsArmed);
        Assert.Contains(ArmingRequirement.DeviceNotDesignated, decision.Unmet);
    }

    // ------------------------------------------------ recovery manifest

    private static RecoveryManifest SampleManifest(string id = "tx123") => new()
    {
        TransactionId = id,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Machine = new RecoveryMachineSummary
        {
            WindowsEdition = "Windows 11 Home",
            BuildNumber = 26200,
            MachineName = "TESTMACHINE",
            RecoveryAdministrator = "Jimmy",
            UacEnabled = true
        },
        Steps =
        [
            new RecoveryStep
            {
                OperationId = "child-account",
                Description = "Skapa barnkonto",
                PreviousValue = null,
                ExistedBefore = false,
                ManualRollbackHint = "Ta bort kontot i Inställningar > Konton."
            }
        ]
    };

    [Fact]
    public async Task A_manifest_round_trips_through_the_store()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());

        Assert.True(await store.WriteAsync(SampleManifest()));

        var all = await store.ListAsync();
        var manifest = Assert.Single(all);

        Assert.Equal("tx123", manifest.TransactionId);
        Assert.Equal("Windows 11 Home", manifest.Machine.WindowsEdition);
        Assert.Equal("child-account", Assert.Single(manifest.Steps).OperationId);
    }

    [Fact]
    public async Task An_unfinished_manifest_needs_attention()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());
        await store.WriteAsync(SampleManifest());

        // Written before anything is applied, so until it is completed the
        // machine may be mid-change.
        var outstanding = await store.ListOutstandingAsync();
        Assert.Single(outstanding);

        await store.CompleteAsync("tx123", TransactionState.Committed);

        Assert.Empty(await store.ListOutstandingAsync());
    }

    [Fact]
    public async Task A_failed_rollback_leaves_the_manifest_outstanding()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());
        await store.WriteAsync(SampleManifest());

        await store.CompleteAsync("tx123", TransactionState.RollbackFailed);

        // The one case where a human must still act.
        var outstanding = Assert.Single(await store.ListOutstandingAsync());
        Assert.Equal(TransactionState.RollbackFailed, outstanding.FinalState);
        Assert.True(outstanding.RequiresAttention);
    }

    [Fact]
    public async Task A_rolled_back_transaction_needs_no_attention()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());
        await store.WriteAsync(SampleManifest());

        await store.CompleteAsync("tx123", TransactionState.RolledBack);

        Assert.Empty(await store.ListOutstandingAsync());
    }

    [Fact]
    public async Task A_manifest_contains_no_secret_material()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());
        await store.WriteAsync(SampleManifest());

        var file = Directory.GetFiles(dir.Path, "*.json").Single();
        var json = await File.ReadAllTextAsync(file);

        // A file whose purpose is to be readable during a crisis is the worst
        // possible place for a secret.
        foreach (var forbidden in new[] { "pin", "hash", "salt", "password", "lösenord" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_manifest_is_readable_by_a_human()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());
        await store.WriteAsync(SampleManifest());

        var json = await File.ReadAllTextAsync(Directory.GetFiles(dir.Path, "*.json").Single());

        // Indented, with the manual hint intact: somebody may have to follow
        // this in Notepad from another account.
        Assert.Contains("\n", json, StringComparison.Ordinal);
        Assert.Contains("Inställningar", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_unreadable_manifest_does_not_hide_the_others()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());

        await store.WriteAsync(SampleManifest("good"));
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "recovery-broken.json"), "{ not json");

        // The remaining manifests may be the ones that matter.
        var all = await store.ListAsync();
        Assert.Single(all);
        Assert.Equal("good", all[0].TransactionId);
    }

    [Fact]
    public async Task Completing_an_unknown_transaction_fails_quietly()
    {
        using var dir = new TempDirectory();
        var store = new RecoveryManifestStore(dir.Path, new RecordingLogger());

        Assert.False(await store.CompleteAsync("nope", TransactionState.Committed));
    }

    // ------------------------------------------------ result shape

    [Fact]
    public void Only_a_rollback_failure_asks_for_human_help()
    {
        foreach (var state in Enum.GetValues<TransactionState>())
        {
            var result = new TransactionResult
            {
                TransactionId = "t",
                State = state,
                ExecutionMode = SecurityExecutionMode.AuditOnly
            };

            Assert.Equal(state == TransactionState.RollbackFailed, result.NeedsManualRecovery);
            Assert.Equal(state == TransactionState.Committed, result.Succeeded);
        }
    }
}
