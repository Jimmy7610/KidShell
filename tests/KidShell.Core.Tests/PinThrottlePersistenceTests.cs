using KidShell.Core.Configuration;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV RETEST 2, ADDITIONAL FINDING — the PIN throttle reset on restart.
///
/// It lived in memory, so a new process started with a clean slate. A child
/// who could close the shell, or make it crash, got their attempts back and
/// the progressive delay priced nothing at all.
/// </summary>
public class PinThrottlePersistenceTests
{
    private static readonly IRuntimeEnvironment Production =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    private static (ParentPinService Service, InMemoryProtectedStateStore Vault, KidShellConfiguration Config)
        Build(FakeTimeProvider time, InMemoryProtectedStateStore? vault = null,
              KidShellConfiguration? configuration = null)
    {
        var store = vault ?? new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        var config = configuration ?? KidShellConfiguration.CreateDefault();
        var logger = new RecordingLogger();

        var service = new ParentPinService(
            new StubState(config),
            Production,
            logger,
            time,
            new ProtectedPinThrottleStore(store, store, logger, time));

        return (service, store, config);
    }

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 29, 19, 0, 0, TimeSpan.Zero));

    private static void Exhaust(ParentPinService service)
    {
        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            service.Verify("000000");
        }
    }

    // --------------------------------------------- the reported defect

    [Fact]
    public void A_restart_during_the_cooldown_is_still_throttled()
    {
        var time = Clock();
        var (first, vault, config) = Build(time);

        Exhaust(first);
        Assert.Equal(PinVerificationResult.Throttled, first.Verify("000000"));

        // A new process, the same machine, the same protected store.
        var (second, _, _) = Build(time, vault, config);

        Assert.Equal(PinVerificationResult.Throttled, second.Verify("000000"));
    }

    [Fact]
    public void The_cooldown_still_expires_after_a_restart()
    {
        // Bounded, and it always ends. A parent must never be locked out of
        // their own computer.
        var time = Clock();
        var (first, vault, config) = Build(time);

        Exhaust(first);

        time.Advance(PinAttemptPolicy.MaximumDelay + TimeSpan.FromSeconds(1));

        var (second, _, _) = Build(time, vault, config);

        Assert.NotEqual(PinVerificationResult.Throttled, second.Verify("000000"));
    }

    [Fact]
    public void Deleting_the_user_configuration_does_nothing_to_the_throttle()
    {
        // The throttle is not in the child's file, so the obvious attack on it
        // is not an attack on this.
        var time = Clock();
        var (first, vault, config) = Build(time);

        Exhaust(first);

        // A fresh configuration, as though the child's own file had been
        // deleted and defaults recreated.
        var (second, _, _) = Build(time, vault, KidShellConfiguration.CreateDefault());

        Assert.Equal(PinVerificationResult.Throttled, second.Verify("000000"));
    }

    // ------------------------------------------------- hostile states

    [Fact]
    public void A_corrupt_throttle_state_does_not_grant_free_attempts()
    {
        var time = Clock();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        vault.Corrupt(ProtectedDocument.PinThrottleState);

        var (service, _, _) = Build(time, vault);

        // Corrupting the file must not be a way to buy attempts. Same shape as
        // deleting the screen-time counter, same answer.
        Assert.Equal(PinVerificationResult.Throttled, service.Verify("000000"));
    }

    [Fact]
    public void An_absurd_future_cooldown_is_bounded_rather_than_obeyed()
    {
        // The value comes off disk, so it comes from whoever can write that
        // file. A cooldown in the year 3000 would lock a parent out forever,
        // which is a worse failure than a few free attempts.
        var time = Clock();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        vault.Seed(ProtectedDocument.PinThrottleState,
            """{"schemaVersion":1,"failedAttemptCount":9999,"cooldownUntilUtc":"3000-01-01T00:00:00+00:00"}""");

        var (service, _, _) = Build(time, vault);

        Assert.Equal(PinVerificationResult.Throttled, service.Verify("000000"));

        time.Advance(PinAttemptPolicy.MaximumDelay + TimeSpan.FromSeconds(1));

        var (later, _, _) = Build(time, vault);

        Assert.NotEqual(PinVerificationResult.Throttled, later.Verify("000000"));
    }

    [Fact]
    public void Winding_the_clock_back_does_not_shorten_the_cooldown()
    {
        var time = Clock();
        var (first, vault, config) = Build(time);

        Exhaust(first);

        // The stored instant is in UTC, so a backwards jump leaves it further
        // in the future - the conservative direction.
        time.Advance(TimeSpan.FromHours(-5));

        var (second, _, _) = Build(time, vault, config);

        Assert.Equal(PinVerificationResult.Throttled, second.Verify("000000"));
    }

    [Fact]
    public void A_future_schema_is_treated_as_a_cooldown()
    {
        var time = Clock();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        vault.Seed(ProtectedDocument.PinThrottleState, """{"schemaVersion":99}""");

        var (service, _, _) = Build(time, vault);

        Assert.Equal(PinVerificationResult.Throttled, service.Verify("000000"));
    }

    // ----------------------------------------------------- recovery

    [Fact]
    public void A_correct_pin_clears_the_persisted_state()
    {
        var time = Clock();
        var configuration = KidShellConfiguration.CreateDefault();
        var (hash, salt) = PinHasher.Hash("123456");
        configuration.ParentPin.Hash = hash;
        configuration.ParentPin.Salt = salt;
        configuration.ParentPin.Iterations = PinHasher.DefaultIterations;

        var (first, vault, _) = Build(time, configuration: configuration);

        first.Verify("000000");
        first.Verify("000000");

        Assert.Equal(PinVerificationResult.Correct, first.Verify("123456"));

        // And a restart starts clean, because the failures were forgiven
        // rather than merely ignored.
        var (second, _, _) = Build(time, vault, configuration);

        Assert.Equal(PinVerificationResult.Correct, second.Verify("123456"));
    }

    [Fact]
    public void Nothing_about_the_attempted_pin_reaches_the_store()
    {
        var time = Clock();
        var (service, vault, _) = Build(time);

        service.Verify("135790");
        service.Verify("246801");

        var document = vault.Read(ProtectedDocument.PinThrottleState) ?? string.Empty;

        Assert.DoesNotContain("135790", document);
        Assert.DoesNotContain("246801", document);
    }

    [Fact]
    public void A_service_with_no_store_still_works()
    {
        // Development, and every existing test that constructs the service
        // without one. The throttle is still enforced in memory.
        var service = new ParentPinService(
            new StubState(KidShellConfiguration.CreateDefault()),
            Production,
            new RecordingLogger(),
            Clock());

        Exhaust(service);

        Assert.Equal(PinVerificationResult.Throttled, service.Verify("000000"));
    }

    private sealed class StubState : IAppStateService
    {
        public StubState(KidShellConfiguration configuration) => Current = configuration;

        public KidShellConfiguration Current { get; private set; }

        public ConfigurationLoadStatus LoadStatus => ConfigurationLoadStatus.Loaded;

        public string? LoadDetail => null;

        public string ConfigurationFilePath => "(in-memory)";

        public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

        public ConfigurationLoadStatus Initialize() => ConfigurationLoadStatus.Loaded;

        public KidShellConfiguration CreateDraft() => Current.Clone();

        public bool Commit(KidShellConfiguration draft)
        {
            Current = draft;
            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(Current));
            return true;
        }

        public bool SaveCurrent() => true;
    }
}
