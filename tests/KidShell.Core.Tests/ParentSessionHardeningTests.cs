using KidShell.Core.Configuration;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV FINDING 05 — three ways the parent gate was softer than it looked.
///
///   A. guessing was free
///   B. Parent Mode never re-locked
///   C. the stored PBKDF2 iteration count had no upper bound
///
/// The third is the least obvious and the most interesting: an unbounded count
/// is not a weak hash, it is a denial of service that the victim triggers by
/// typing their own PIN.
/// </summary>
public class ParentSessionHardeningTests
{
    // --------------------------------------------------- 5A: throttling

    [Fact]
    public void The_first_few_mistakes_are_not_punished()
    {
        // A parent mistyping twice while a child watches is ordinary.
        for (var failures = 1; failures <= PinAttemptPolicy.FreeAttempts; failures++)
        {
            Assert.Equal(TimeSpan.Zero, PinAttemptPolicy.DelayAfter(failures));
        }
    }

    [Fact]
    public void Guessing_stops_being_free_after_that()
    {
        var first = PinAttemptPolicy.DelayAfter(PinAttemptPolicy.FreeAttempts + 1);
        var second = PinAttemptPolicy.DelayAfter(PinAttemptPolicy.FreeAttempts + 2);

        Assert.True(first > TimeSpan.Zero);
        Assert.True(second > first);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(int.MaxValue)]
    public void The_delay_is_bounded_and_never_goes_negative(int failures)
    {
        // A parent must never be locked out of their own computer, and an
        // overflowing shift would turn the throttle OFF at exactly the point
        // it mattered most.
        var delay = PinAttemptPolicy.DelayAfter(failures);

        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= PinAttemptPolicy.MaximumDelay);
    }

    [Fact]
    public void A_throttled_attempt_is_refused_until_the_clock_moves()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var throttle = new PinAttemptThrottle(time);

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            throttle.RecordFailure();
        }

        Assert.False(throttle.Evaluate().IsAllowed);

        time.Advance(PinAttemptPolicy.MaximumDelay + TimeSpan.FromSeconds(1));

        Assert.True(throttle.Evaluate().IsAllowed);
    }

    [Fact]
    public void A_correct_pin_clears_the_throttle()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var throttle = new PinAttemptThrottle(time);

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            throttle.RecordFailure();
        }

        throttle.RecordSuccess();

        Assert.True(throttle.Evaluate().IsAllowed);
        Assert.Equal(0, throttle.ConsecutiveFailures);
    }

    [Fact]
    public void The_service_reports_throttling_rather_than_a_wrong_pin()
    {
        // The distinction the UI needs. Telling a parent who mistyped four
        // times that their correct PIN is wrong is how a product teaches
        // somebody to distrust it.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var service = Service(time, out _);

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            Assert.Equal(PinVerificationResult.Incorrect, service.Verify("000000"));
        }

        Assert.Equal(PinVerificationResult.Throttled, service.Verify("000000"));
        Assert.True(service.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void A_throttled_attempt_does_not_check_the_pin_at_all()
    {
        // Even the right PIN waits. Otherwise the throttle would be an oracle:
        // "this one was refused differently" is the answer an attacker wants.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var service = Service(time, out var configuration);

        var (hash, salt) = PinHasher.Hash("123456");
        configuration.ParentPin.Hash = hash;
        configuration.ParentPin.Salt = salt;
        configuration.ParentPin.Iterations = PinHasher.DefaultIterations;

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            service.Verify("000000");
        }

        Assert.Equal(PinVerificationResult.Throttled, service.Verify("123456"));

        time.Advance(PinAttemptPolicy.MaximumDelay + TimeSpan.FromSeconds(1));

        Assert.Equal(PinVerificationResult.Correct, service.Verify("123456"));
    }

    // ------------------------------------------------------ 5B: relocking

    [Fact]
    public void A_session_expires_after_the_inactivity_window()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var session = new ParentSession(new RecordingLogger(), time);

        session.Begin();
        Assert.True(session.IsUnlocked);

        time.Advance(ParentSession.InactivityTimeout + TimeSpan.FromSeconds(1));

        Assert.False(session.IsUnlocked);
        Assert.Equal(ParentSessionEndReason.Inactivity, session.LastEndReason);
    }

    [Fact]
    public void Activity_restarts_the_window_rather_than_extending_a_fixed_life()
    {
        // A parent doing a real setup pass must not be thrown out mid-sentence.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var session = new ParentSession(new RecordingLogger(), time);

        session.Begin();

        for (var i = 0; i < 10; i++)
        {
            time.Advance(ParentSession.InactivityTimeout - TimeSpan.FromMinutes(1));
            session.Touch();
        }

        Assert.True(session.IsUnlocked);
    }

    [Fact]
    public void Ending_raises_once_and_only_once()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var session = new ParentSession(new RecordingLogger(), time);

        var ended = 0;
        session.Ended += (_, _) => ended++;

        session.Begin();
        time.Advance(ParentSession.InactivityTimeout + TimeSpan.FromSeconds(1));

        session.Evaluate();
        session.Evaluate();
        session.Evaluate();

        Assert.Equal(1, ended);
    }

    [Fact]
    public void Returning_to_child_mode_ends_the_session()
    {
        var session = new ParentSession(new RecordingLogger(),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero)));

        session.Begin();
        session.End(ParentSessionEndReason.ReturnedToChild);

        Assert.False(session.IsUnlocked);
        Assert.Equal(ParentSessionEndReason.ReturnedToChild, session.LastEndReason);
    }

    [Fact]
    public void Touching_a_locked_session_does_not_reopen_it()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 19, 0, 0, TimeSpan.Zero));
        var session = new ParentSession(new RecordingLogger(), time);

        session.Begin();
        session.End(ParentSessionEndReason.Explicit);
        session.Touch();

        Assert.False(session.IsUnlocked);
    }

    // ------------------------------------------- 5C: iteration bounds

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(1)]
    [InlineData(PinHasher.MinimumIterations - 1)]
    [InlineData(PinHasher.MaximumIterations + 1)]
    [InlineData(int.MaxValue)]
    public void An_out_of_range_iteration_count_is_refused(int iterations) =>
        Assert.False(PinHasher.IsAcceptableIterationCount(iterations));

    [Theory]
    [InlineData(PinHasher.MinimumIterations)]
    [InlineData(PinHasher.DefaultIterations)]
    [InlineData(PinHasher.MaximumIterations)]
    public void An_in_range_iteration_count_is_accepted(int iterations) =>
        Assert.True(PinHasher.IsAcceptableIterationCount(iterations));

    [Fact]
    public void A_tampered_iteration_count_never_reaches_the_derivation()
    {
        // The measurement that matters. int.MaxValue rounds of PBKDF2 would
        // pin a core for minutes; if this test is fast, the bound was checked
        // before the work was started rather than after.
        var (hash, salt) = PinHasher.Hash("123456");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var accepted = PinHasher.Verify("123456", hash, salt, int.MaxValue);
        started.Stop();

        Assert.False(accepted);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2),
            $"the derivation ran before the bound was checked; it took {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void A_hash_written_at_the_default_count_still_verifies()
    {
        // The bounds must not lock out configurations this build wrote itself.
        var (hash, salt) = PinHasher.Hash("123456");

        Assert.True(PinHasher.Verify("123456", hash, salt, PinHasher.DefaultIterations));
        Assert.False(PinHasher.Verify("654321", hash, salt, PinHasher.DefaultIterations));
    }

    [Fact]
    public void The_minimum_is_below_the_default_so_older_hashes_still_open()
    {
        Assert.True(PinHasher.MinimumIterations <= PinHasher.DefaultIterations);
        Assert.True(PinHasher.MaximumIterations >= PinHasher.DefaultIterations);
    }

    // ------------------------------------------------------------ harness

    private static ParentPinService Service(TimeProvider time, out KidShellConfiguration configuration)
    {
        configuration = KidShellConfiguration.CreateDefault();

        return new ParentPinService(
            new StubAppStateService(configuration),
            new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release"),
            new RecordingLogger(),
            time);
    }

    private sealed class StubAppStateService : IAppStateService
    {
        public StubAppStateService(KidShellConfiguration configuration) => Current = configuration;

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
