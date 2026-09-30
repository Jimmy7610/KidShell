namespace KidShell.Core.Security;

/// <summary>
/// How long to wait after a wrong PIN.
///
/// OPSV FINDING 05A — guessing was free. A six-digit PIN is a million
/// candidates, and nothing stopped a child working through them at whatever
/// rate they could tap. The keypad is on screen, the child has the machine,
/// and they have all afternoon.
///
/// WHAT THIS IS NOT
/// ----------------
/// It is not a lockout. A parent who fat-fingers the PIN five times while a
/// child watches must still be able to get in, and a product that can
/// permanently lock the adult out of their own computer has invented a worse
/// problem than the one it solved. So the delay is bounded, and it always
/// expires.
///
/// It is also not a defence against someone with a debugger. It raises the
/// cost of the only attack that is actually available to a six-year-old with
/// a touchscreen, which is the attack worth pricing.
/// </summary>
public static class PinAttemptPolicy
{
    /// <summary>
    /// Wrong answers allowed before any delay.
    ///
    /// Three, because mistyping twice is ordinary and a parent should not be
    /// made to feel accused for it.
    /// </summary>
    public const int FreeAttempts = 3;

    /// <summary>The first delay, once the free attempts are gone.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest a parent is ever made to wait.
    ///
    /// Two minutes. Long enough that exhausting a million codes would take
    /// years; short enough that a parent who genuinely forgot and is now
    /// trying to remember is not locked out of their evening.
    /// </summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to refuse for, after a given number of consecutive failures.
    ///
    /// Doubling from the first delay, capped. At the cap, a million guesses
    /// takes about four years of continuous tapping.
    /// </summary>
    public static TimeSpan DelayAfter(int consecutiveFailures)
    {
        if (consecutiveFailures <= FreeAttempts)
        {
            return TimeSpan.Zero;
        }

        var steps = consecutiveFailures - FreeAttempts - 1;

        // Computed in ticks against the cap rather than by shifting, so a
        // large failure count cannot overflow into a negative delay - which
        // would turn the throttle off exactly when it was needed most.
        var scale = Math.Min(steps, 20);
        var ticks = FirstDelay.Ticks * (1L << (int)scale);

        return ticks >= MaximumDelay.Ticks || ticks < 0
            ? MaximumDelay
            : TimeSpan.FromTicks(ticks);
    }
}

/// <summary>What a throttle says about the next attempt.</summary>
/// <param name="IsAllowed">Whether a PIN may be checked right now.</param>
/// <param name="RetryAfter">How long until it may be, when it may not.</param>
/// <param name="ConsecutiveFailures">How many wrong answers led here.</param>
public sealed record PinAttemptDecision(bool IsAllowed, TimeSpan RetryAfter, int ConsecutiveFailures);

/// <summary>
/// Counts consecutive wrong PINs and refuses for a while.
///
/// In memory on purpose. Persisting it would let a child clear the throttle by
/// deleting a file - the same shape of hole as the screen-time counter - and
/// the attack it prices is a child sitting at the keypad, who does not get to
/// restart the shell between guesses without a parent noticing.
///
/// The clock is injected so the delays can be tested without waiting for them.
/// </summary>
public sealed class PinAttemptThrottle
{
    private readonly TimeProvider _time;

    private int _consecutiveFailures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public PinAttemptThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Restores a throttle that survived a restart.
    ///
    /// OPSV RETEST 2, ADDITIONAL FINDING. Without this, a child who could
    /// close or crash the shell got their attempts back and the progressive
    /// delay priced nothing.
    /// </summary>
    public void Restore(int consecutiveFailures, DateTimeOffset? cooldownUntilUtc)
    {
        _consecutiveFailures = Math.Max(0, consecutiveFailures);
        _blockedUntil = cooldownUntilUtc ?? DateTimeOffset.MinValue;
    }

    /// <summary>The state to persist, so a restart does not clear it.</summary>
    public (int Failures, DateTimeOffset? Until) Snapshot() =>
        (_consecutiveFailures, _blockedUntil == DateTimeOffset.MinValue ? null : _blockedUntil);

    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>Whether a PIN may be checked now, and how long until it may be.</summary>
    public PinAttemptDecision Evaluate()
    {
        var now = _time.GetUtcNow();

        if (now >= _blockedUntil)
        {
            return new PinAttemptDecision(true, TimeSpan.Zero, _consecutiveFailures);
        }

        return new PinAttemptDecision(false, _blockedUntil - now, _consecutiveFailures);
    }

    /// <summary>Records a wrong PIN and starts the next delay.</summary>
    public PinAttemptDecision RecordFailure()
    {
        _consecutiveFailures++;

        var delay = PinAttemptPolicy.DelayAfter(_consecutiveFailures);
        _blockedUntil = delay > TimeSpan.Zero ? _time.GetUtcNow() + delay : DateTimeOffset.MinValue;

        return new PinAttemptDecision(delay <= TimeSpan.Zero, delay, _consecutiveFailures);
    }

    /// <summary>Records a correct PIN. Everything goes back to normal.</summary>
    public void RecordSuccess()
    {
        _consecutiveFailures = 0;
        _blockedUntil = DateTimeOffset.MinValue;
    }
}
