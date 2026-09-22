using System.Reflection;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// That the recovery manifest is on disk before anything is changed, and that
/// no call sequence exists which gets it the other way round.
///
/// WHY ORDER IS THE WHOLE THING
/// ----------------------------
/// A manifest written after a change is a description of a machine somebody is
/// already locked out of. The safe sequence is:
///
///     preflight all -> capture all state -> persist the manifest -> apply
///
/// and the last two must not be able to swap. That used to rest on an external
/// caller remembering to write it, and no caller existed - so the ordering was
/// a comment, not a guarantee. The store is now a required constructor
/// dependency and the write happens inside the coordinator, which is the only
/// entry point.
/// </summary>
public class SecurityOrderingTests
{
    private static SecurityTransaction Build(
        IRecoveryManifestStore store,
        params ISecurityOperation[] operations) =>
        new(operations, store, TestMachineSummary.Create(), new RecordingLogger());

    [Fact]
    public async Task The_manifest_is_written_before_the_first_apply()
    {
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal };
        var b = new FakeOperation("b") { Journal = journal };

        await Build(store, a, b).ExecuteAsync(ApplyContextForTests.Create());

        // Observed, not asserted from the source: the store and the operations
        // write to the same list in the order they actually ran.
        var manifestAt = journal.IndexOf("manifest-write");
        var firstApplyAt = journal.FindIndex(e => e.StartsWith("apply:", StringComparison.Ordinal));

        Assert.True(manifestAt >= 0, "the manifest was never written");
        Assert.True(firstApplyAt >= 0, "nothing was ever applied");
        Assert.True(manifestAt < firstApplyAt,
            $"the manifest was written after the first apply. Journal: {string.Join(" -> ", journal)}");
    }

    [Fact]
    public async Task Every_snapshot_is_captured_before_the_manifest_is_written()
    {
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal);

        var a = new FakeOperation("a") { Journal = journal };
        var b = new FakeOperation("b") { Journal = journal };

        await Build(store, a, b).ExecuteAsync(ApplyContextForTests.Create());

        var manifestAt = journal.IndexOf("manifest-write");
        var lastSnapshotAt = journal.FindLastIndex(e => e.StartsWith("snapshot:", StringComparison.Ordinal));

        // The manifest is built FROM the snapshots, so a manifest written
        // early would be missing the previous values it exists to record.
        Assert.True(lastSnapshotAt < manifestAt,
            $"a snapshot was taken after the manifest. Journal: {string.Join(" -> ", journal)}");

        var manifest = Assert.Single(store.Written);
        Assert.Equal(["a", "b"], manifest.Steps.Select(s => s.OperationId));
    }

    [Fact]
    public async Task A_failed_manifest_write_stops_the_transaction_before_anything_is_applied()
    {
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal, writeSucceeds: false);

        var a = new FakeOperation("a") { Journal = journal };
        var b = new FakeOperation("b") { Journal = journal };

        var result = await Build(store, a, b).ExecuteAsync(ApplyContextForTests.Create());

        // No way back means no way forward.
        Assert.Equal(TransactionState.Refused, result.State);
        Assert.False(result.Succeeded);
        Assert.Equal(0, a.ApplyCount);
        Assert.Equal(0, b.ApplyCount);
        Assert.Empty(result.AppliedOperations);
        Assert.DoesNotContain(journal, e => e.StartsWith("apply:", StringComparison.Ordinal));

        // And the parent is told why, in Swedish, without an error code.
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("Återställningsfilen", result.FailureMessage);
    }

    [Fact]
    public async Task A_manifest_store_that_throws_also_stops_the_transaction()
    {
        var journal = new List<string>();
        var store = new RecordingManifestStore(journal, throwOnWrite: true);
        var a = new FakeOperation("a") { Journal = journal };

        // A store that throws is a store that did not write, and must not be
        // mistaken for one that did.
        var result = await Build(store, a).ExecuteAsync(ApplyContextForTests.Create());

        Assert.Equal(TransactionState.Refused, result.State);
        Assert.Equal(0, a.ApplyCount);
    }

    [Fact]
    public async Task The_outcome_is_stamped_onto_the_manifest_afterwards()
    {
        var store = new RecordingManifestStore([]);

        await Build(store, new FakeOperation("a")).ExecuteAsync(ApplyContextForTests.Create());

        // Until this happens the manifest reads "unfinished", which is what
        // makes it show up as needing attention.
        Assert.Equal(TransactionState.Committed, Assert.Single(store.Completed));
    }

    [Fact]
    public async Task A_refused_manifest_write_is_never_stamped_as_finished()
    {
        var store = new RecordingManifestStore([], writeSucceeds: false);

        await Build(store, new FakeOperation("a")).ExecuteAsync(ApplyContextForTests.Create());

        // Nothing was written, so there is nothing to complete - and claiming
        // otherwise would invent a manifest that does not exist.
        Assert.Empty(store.Completed);
    }

    [Fact]
    public void A_transaction_cannot_be_built_without_somewhere_to_write_recovery()
    {
        // The ordering guarantee starts here: there is no overload, no null
        // default, and no second entry point, so "apply now, write the
        // manifest later" is not an expressible call sequence.
        Assert.Throws<ArgumentNullException>(() =>
            new SecurityTransaction([], null!, TestMachineSummary.Create(), new RecordingLogger()));

        Assert.Throws<ArgumentNullException>(() =>
            new SecurityTransaction([], new RecordingManifestStore([]), null!, new RecordingLogger()));
    }

    [Fact]
    public void Every_public_constructor_demands_a_recovery_store()
    {
        // Structural, so a future overload that quietly drops the requirement
        // fails here rather than in production.
        var constructors = typeof(SecurityTransaction).GetConstructors();

        Assert.NotEmpty(constructors);

        foreach (var constructor in constructors)
        {
            Assert.Contains(constructor.GetParameters(),
                p => p.ParameterType == typeof(IRecoveryManifestStore));
        }
    }

    [Fact]
    public void ExecuteAsync_is_the_only_way_to_run_operations()
    {
        // If a second entry point appears, the manifest ordering is only
        // guaranteed on whichever one remembered to write it - which is
        // exactly the arrangement this work replaced.
        var entryPoints = typeof(SecurityTransaction)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType))
            .Select(m => m.Name)
            .Distinct()
            .ToArray();

        Assert.Equal([nameof(SecurityTransaction.ExecuteAsync)], entryPoints);
    }
}

/// <summary>
/// That the machine half of arming cannot be asserted by a caller.
///
/// The old <c>ArmingRequest</c> carried five machine conditions as
/// <c>required bool</c> properties, so
/// <c>new ArmingRequest { ProcessElevated = true, RecoveryProven = true, … }</c>
/// was a complete and compiling claim that the machine was ready. The day an
/// Apply factory exists, that one expression would have been the entire
/// distance between audit mode and changing Windows.
/// </summary>
public class ArmingTrustBoundaryTests
{
    [Fact]
    public void Machine_facts_have_no_public_constructor()
    {
        // The core of it: a caller cannot write the type at all.
        Assert.Empty(typeof(VerifiedMachineFacts).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Machine_facts_cannot_be_modified_after_verification()
    {
        // No init setters either, so `verified with { ProcessElevated = true }`
        // is not available as a way around the constructor.
        var settable = typeof(VerifiedMachineFacts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null)
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(settable);
    }

    [Fact]
    public void The_only_freely_available_facts_are_all_false()
    {
        var facts = VerifiedMachineFacts.NothingVerified;

        // The safe default: not knowing is not permission.
        Assert.False(facts.ProcessElevated);
        Assert.False(facts.ParentPinConfigured);
        Assert.False(facts.DeviceDesignated);
        Assert.False(facts.RecoveryProven);
        Assert.False(facts.CapabilitySatisfied);
    }

    [Fact]
    public void Unverified_facts_leave_every_machine_condition_unmet()
    {
        // Full human consent, and it arms nothing, because consent was never
        // the part that was in doubt.
        var (decision, context) = SecurityArming.TryArm(
            new ParentAuthorization { Authenticated = true, TransactionConfirmed = true },
            VerifiedMachineFacts.NothingVerified,
            TestRuntime.Production,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()));

        Assert.False(decision.IsArmed);
        Assert.Contains(ArmingRequirement.NotElevated, decision.Unmet);
        Assert.Contains(ArmingRequirement.RecoveryNotProven, decision.Unmet);
        Assert.Contains(ArmingRequirement.DeviceNotDesignated, decision.Unmet);
        Assert.Contains(ArmingRequirement.ParentPinMissing, decision.Unmet);
        Assert.Contains(ArmingRequirement.CapabilityMissing, decision.Unmet);

        Assert.Equal(SecurityExecutionMode.AuditOnly, context.Mode);
    }

    [Fact]
    public void KidShell_implements_none_of_the_machine_fact_sources()
    {
        // This is what makes the boundary real rather than decorative: the
        // verifier exists, and there is nothing in the product to feed it.
        // Writing the first implementation is a visible, reviewable act.
        var sources = new[]
        {
            typeof(IDeviceDesignationSource),
            typeof(IRecoveryReadinessSource),
            typeof(IParentPinSource)
        };

        var implementations = typeof(SecurityArming).Assembly
            .GetTypes()
            .Where(t => t is { IsInterface: false, IsAbstract: false })
            .Where(t => sources.Any(s => s.IsAssignableFrom(t)))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(implementations);
    }

    [Fact]
    public void Elevation_is_read_from_the_machine_not_claimed_by_a_caller()
    {
        // It comes from the capability analysis, which reads the process
        // token. There is no parameter through which to assert it.
        var facts = MachineFactVerifier.Verify(
            TestRuntime.Production,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()) with { IsProcessElevated = false },
            new StubDesignationSource(true),
            new StubRecoverySource(true),
            new StubParentPinSource(true),
            [RequiredCapability.None]);

        Assert.False(facts.ProcessElevated);
    }

    [Fact]
    public void A_development_machine_cannot_be_designated_however_loudly_a_source_claims_it()
    {
        var facts = MachineFactVerifier.Verify(
            TestRuntime.Development,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()),
            new StubDesignationSource(designated: true),
            new StubRecoverySource(true),
            new StubParentPinSource(true),
            [RequiredCapability.None]);

        // The source said yes. The verifier does not care.
        Assert.False(facts.DeviceDesignated);
    }

    [Fact]
    public void Capability_satisfaction_is_computed_against_what_the_machine_supports()
    {
        // Home has no Assigned Access, so a transaction needing it is not
        // satisfied - regardless of what anybody would like to assert.
        var home = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        var facts = MachineFactVerifier.Verify(
            TestRuntime.Production,
            home,
            new StubDesignationSource(true),
            new StubRecoverySource(true),
            new StubParentPinSource(true),
            [RequiredCapability.AssignedAccess]);

        Assert.False(facts.CapabilitySatisfied);
        Assert.False(MachineFactVerifier.Supports(home, RequiredCapability.AssignedAccess));
    }

    [Fact]
    public void An_unknown_capability_is_never_treated_as_satisfied()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());

        // An undetermined reading is not permission.
        Assert.False(MachineFactVerifier.Supports(capabilities, (RequiredCapability)9999));
    }

    [Fact]
    public void Even_fully_verified_facts_cannot_produce_an_apply_context()
    {
        var facts = MachineFactVerifier.Verify(
            TestRuntime.Production,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()) with { IsProcessElevated = true },
            new StubDesignationSource(true),
            new StubRecoverySource(true),
            new StubParentPinSource(true),
            [RequiredCapability.None]);

        var (decision, context) = SecurityArming.TryArm(
            new ParentAuthorization { Authenticated = true, TransactionConfirmed = true },
            facts,
            TestRuntime.Production,
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro()));

        // Everything a human and a machine could offer, and the build still
        // refuses, because the feature is not compiled in.
        Assert.False(decision.IsArmed);
        Assert.Contains(ArmingRequirement.SecurityFeatureNotBuilt, decision.Unmet);
        Assert.Equal(SecurityExecutionMode.AuditOnly, context.Mode);
    }

    [Fact]
    public void No_public_api_anywhere_returns_an_apply_context()
    {
        // The guarantee the reflection harness deliberately steps around:
        // nothing a caller can *call* hands back an Apply context.
        var factories = typeof(SecurityArming).Assembly
            .GetTypes()
            .Where(t => t.IsPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.ReturnType == typeof(SecurityExecutionContext))
            .ToArray();

        foreach (var factory in factories)
        {
            Assert.Equal(nameof(SecurityExecutionContext.AuditOnly), factory.Name);
        }

        // And the one that exists really does return AuditOnly.
        Assert.Equal(SecurityExecutionMode.AuditOnly, SecurityExecutionContext.AuditOnly().Mode);
    }
}
