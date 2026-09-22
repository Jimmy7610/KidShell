using System.Reflection;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Builds an Apply-mode <see cref="SecurityExecutionContext"/> by reflection,
/// for tests only.
///
/// WHY THIS IS NOT A HOLE IN THE DESIGN
/// ------------------------------------
/// The product guarantee is that there is no public, internal or test-visible
/// *API* that constructs an Apply context: the constructor is private and the
/// single public factory returns AuditOnly. That is still exactly true. This
/// class does not use an API — it reaches past the language with reflection,
/// which is deliberate and is the point: forging the token has to be an
/// obvious, ugly, test-only act rather than something a future caller can do
/// by writing ordinary C#.
///
/// It also buys nothing on a real machine. An Apply context is a token that
/// says "you may change Windows"; changing Windows still needs an
/// <see cref="ISecurityOperation"/>, and the only implementation in the entire
/// solution is <c>FakeOperation</c>, which mutates two integer counters in the
/// test assembly. So these tests exercise the coordinator's apply, verify,
/// cancel and rollback logic in full while touching nothing outside the
/// process.
///
/// Before this existed, none of that logic was reachable by any test at all,
/// which is precisely how a cancellation path that skipped rollback survived
/// review.
/// </summary>
internal static class ApplyContextForTests
{
    public static SecurityExecutionContext Create()
    {
        var constructor = typeof(SecurityExecutionContext).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(SecurityExecutionMode)],
            modifiers: null);

        Assert.NotNull(constructor);

        var context = (SecurityExecutionContext)constructor!.Invoke([SecurityExecutionMode.Apply]);

        // If this ever stops being Apply, every cancellation test below would
        // silently pass by taking the refusal path instead.
        Assert.Equal(SecurityExecutionMode.Apply, context.Mode);

        return context;
    }
}

/// <summary>
/// An in-memory <see cref="IRecoveryManifestStore"/> that records when it was
/// written relative to everything else.
///
/// The shared <see cref="Journal"/> is the mechanism for proving ordering: the
/// store and the operations append to the same list, so a test can assert that
/// "manifest" appears before the first "apply" rather than taking the
/// coordinator's word for it.
/// </summary>
internal sealed class RecordingManifestStore : IRecoveryManifestStore
{
    private readonly bool _writeSucceeds;
    private readonly bool _throwOnWrite;

    public RecordingManifestStore(
        List<string> journal,
        bool writeSucceeds = true,
        bool throwOnWrite = false)
    {
        Journal = journal;
        _writeSucceeds = writeSucceeds;
        _throwOnWrite = throwOnWrite;
    }

    /// <summary>
    /// Cancelled once the manifest has been written. Puts the cancellation
    /// exactly between the manifest and the first Apply.
    /// </summary>
    public CancellationTokenSource? CancelAfterWrite { get; init; }

    public List<string> Journal { get; }

    public List<RecoveryManifest> Written { get; } = [];

    public List<TransactionState> Completed { get; } = [];

    public Task<bool> WriteAsync(RecoveryManifest manifest, CancellationToken cancellationToken = default)
    {
        Journal.Add("manifest-write");

        if (_throwOnWrite)
        {
            throw new IOException("the recovery directory is not writable");
        }

        if (!_writeSucceeds)
        {
            return Task.FromResult(false);
        }

        Written.Add(manifest);
        CancelAfterWrite?.Cancel();
        return Task.FromResult(true);
    }

    public Task<bool> CompleteAsync(
        string transactionId,
        TransactionState finalState,
        CancellationToken cancellationToken = default)
    {
        Journal.Add($"manifest-complete:{finalState}");
        Completed.Add(finalState);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<RecoveryManifest>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecoveryManifest>>(Written);

    public Task<IReadOnlyList<RecoveryManifest>> ListOutstandingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecoveryManifest>>([.. Written.Where(m => m.RequiresAttention)]);
}

/// <summary>Machine facts for a manifest. Invented, and never read from this machine.</summary>
internal static class TestMachineSummary
{
    public static RecoveryMachineSummary Create() => new()
    {
        WindowsEdition = "Windows 11 Home",
        BuildNumber = 26200,
        MachineName = "TESTMACHINE",
        RecoveryAdministrator = "TestAdmin",
        UacEnabled = true
    };
}

/// <summary>
/// Machine-fact sources for the arming tests.
///
/// These exist only here. KidShell itself implements none of the three
/// interfaces, which is what makes <see cref="VerifiedMachineFacts"/>
/// unobtainable in the product — a structural test asserts it.
/// </summary>
internal sealed class StubDesignationSource : IDeviceDesignationSource
{
    public StubDesignationSource(bool designated) => IsDesignatedKidShellDevice = designated;

    public bool IsDesignatedKidShellDevice { get; }
}

internal sealed class StubRecoverySource : IRecoveryReadinessSource
{
    public StubRecoverySource(bool proven) => IsRecoveryProven = proven;

    public bool IsRecoveryProven { get; }
}

internal sealed class StubParentPinSource : IParentPinSource
{
    public StubParentPinSource(bool configured) => IsParentPinConfigured = configured;

    public bool IsParentPinConfigured { get; }
}
