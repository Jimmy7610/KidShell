using System.Globalization;
using System.Text.Json;
using KidShell.Core.ScreenTime;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// What the privileged side will accept as the NEXT value of a protected
/// document, given the one it already holds.
///
/// THE SERVICE IS NOT A FILE WRITER
/// --------------------------------
/// This is the part that makes a child-writable enforcement document safe.
/// Without it, routing the screen-time counter through a LocalSystem service
/// achieves nothing at all: the child's process would simply ask SYSTEM to
/// write UsedSeconds = 0, and the protected store would protect a number the
/// child chose. Same for the PIN throttle, where the interesting value to
/// write is FailedAttemptCount = 0.
///
/// So the authority that may write these documents may only write them in one
/// direction. Every rule below has the same shape: a child's session may make
/// the child's situation stricter, and may not make it looser. Anything that
/// would loosen it needs a different authority and a different operation,
/// where the privileged side computes the new value itself rather than
/// accepting one.
///
/// WHERE THESE RULES RUN
/// ---------------------
/// On the privileged side, against the document the privileged side read.
/// Running them in the child's process would be asking the thing being
/// constrained whether it feels constrained.
/// </summary>
public static class ProtectedStateTransitionRules
{
    /// <summary>A day cannot hold more seconds than it has.</summary>
    public const int MaxUsedSecondsPerDay = 24 * 60 * 60;

    /// <summary>Bonus minutes beyond this are not a grant, they are a disabled limit.</summary>
    public const int MaxBonusMinutes = 24 * 60;

    /// <summary>
    /// How far the sequence may advance in one write.
    ///
    /// Forward-only is not enough on its own: a caller that jumps the
    /// sequence to int.MaxValue once would make every honest write afterwards
    /// look like a rollback, which is a denial of service against the parent
    /// rather than against the child. So the advance is bounded as well as
    /// directional.
    /// </summary>
    public const int MaxSequenceAdvance = 1_000;

    /// <summary>Beyond this a clock-event count is not evidence, it is noise.</summary>
    public const int MaxSuspiciousClockEvents = 1_000_000;

    // ------------------------------------------------------- screen time

    /// <summary>
    /// Whether a proposed counter may replace the one on disk.
    ///
    /// <paramref name="current"/> is null the first time anything is written.
    /// </summary>
    public static BrokerDecision ScreenTime(
        ScreenTimeState? current,
        ScreenTimeState? proposed,
        BrokerAuthority authority)
    {
        if (proposed is null)
        {
            return Deny("The counter could not be read as a counter.");
        }

        if (proposed.SchemaVersion != ScreenTimeState.CurrentSchemaVersion)
        {
            return Deny("The counter is not in the schema this build writes.");
        }

        if (!IsLocalDateKey(proposed.LocalDate))
        {
            return Deny("The counter names a day that is not a day.");
        }

        if (proposed.UsedSeconds is < 0 or > MaxUsedSecondsPerDay)
        {
            return Deny("Used seconds are outside one day.");
        }

        if (proposed.BonusMinutes is < 0 or > MaxBonusMinutes)
        {
            return Deny("Bonus minutes are outside one day.");
        }

        if (proposed.SuspiciousClockEvents is < 0 or > MaxSuspiciousClockEvents)
        {
            return Deny("The clock-event count is out of range.");
        }

        if (!Enum.IsDefined(proposed.SessionState))
        {
            return Deny("The session state is not one this build knows.");
        }

        if (current is null)
        {
            // Nothing recorded yet, so there is nothing to weaken. Still
            // bounded by the range checks above.
            return Allow("First write.");
        }

        if (proposed.Sequence < current.Sequence)
        {
            return Deny("The sequence number went backwards.");
        }

        if (proposed.Sequence > current.Sequence + MaxSequenceAdvance)
        {
            return Deny("The sequence number jumped further than a session can.");
        }

        if (proposed.SuspiciousClockEvents < current.SuspiciousClockEvents)
        {
            // Evidence about the machine. A write may add to it and never
            // subtract: erasing the record of a clock change is exactly what
            // somebody who moved the clock would want to do next.
            return Deny("The clock-event count was lowered.");
        }

        var dayComparison = string.CompareOrdinal(proposed.LocalDate, current.LocalDate);

        if (current.LocalDate.Length > 0 && dayComparison < 0)
        {
            // yyyy-MM-dd compares as dates, so this is a day going backwards.
            // On an honest machine the engine refuses to roll over backwards;
            // reaching here means something else wrote the file.
            return Deny("The counter moved to an earlier day.");
        }

        if (dayComparison > 0)
        {
            // A new day. The allowance starts again, which is the one moment
            // used seconds are allowed to fall - so everything a parent
            // granted for YESTERDAY has to be gone with it, or "wait until
            // tomorrow" would carry an unlimited day forward.
            if (proposed.BonusMinutes != 0 || proposed.UnlimitedForToday)
            {
                return Deny("A new day cannot start with yesterday's grants.");
            }

            return Allow("A new day.");
        }

        // Same day from here on.
        if (proposed.UsedSeconds < current.UsedSeconds)
        {
            return Deny("Used time cannot go down within a day.");
        }

        if (authority == BrokerAuthority.ChildSessionRestricted)
        {
            if (proposed.BonusMinutes > current.BonusMinutes)
            {
                return Deny("Extra minutes are granted by a parent, not requested.");
            }

            if (proposed.UnlimitedForToday && !current.UnlimitedForToday)
            {
                return Deny("Lifting today's limit is a parent's decision.");
            }
        }

        return Allow("Within the day, in the stricter direction.");
    }

    // ------------------------------------------------------ pin throttle

    /// <summary>
    /// Whether a proposed throttle may replace the one on disk.
    ///
    /// The asymmetry is the whole point. More failures and a longer cooldown
    /// are accepted from the session being throttled; fewer failures and a
    /// shorter cooldown are not, because a process that can forgive its own
    /// failures has not been throttled.
    /// </summary>
    public static BrokerDecision PinThrottle(
        PinThrottleState? current,
        PinThrottleState? proposed,
        BrokerAuthority authority,
        DateTimeOffset now)
    {
        if (proposed is null)
        {
            return Deny("The throttle could not be read as a throttle.");
        }

        if (proposed.SchemaVersion != PinThrottleState.CurrentSchemaVersion)
        {
            return Deny("The throttle is not in the schema this build writes.");
        }

        if (proposed.FailedAttemptCount is < 0 or > MaxFailedAttempts)
        {
            return Deny("The failure count is out of range.");
        }

        var ceiling = now + PinAttemptPolicy.MaximumDelay + ClockTolerance;

        if (proposed.CooldownUntilUtc is { } until && until > ceiling)
        {
            // A cooldown in the year 3000 locks a parent out of their own
            // computer, which is a worse outcome than a few free attempts.
            // Bounded here as well as on load, because the load-time clamp
            // protects the reader and this protects the file.
            return Deny("The cooldown is longer than the policy allows.");
        }

        if (current is null)
        {
            return Allow("First write.");
        }

        if (authority != BrokerAuthority.ChildSessionRestricted)
        {
            // A parent capability or an administrator may forgive failures.
            // That is what "the parent got in" means.
            return Allow("Cleared by an authority that may clear it.");
        }

        if (proposed.FailedAttemptCount < current.FailedAttemptCount)
        {
            return Deny("Failed attempts cannot be forgiven by the session that made them.");
        }

        if (current.CooldownUntilUtc is { } active && active > now)
        {
            if (proposed.CooldownUntilUtc is not { } next || next < active)
            {
                return Deny("An active cooldown cannot be shortened or cleared.");
            }
        }

        if (proposed.LastFailureUtc < current.LastFailureUtc)
        {
            return Deny("The last failure moved backwards.");
        }

        return Allow("More failures, or the same.");
    }

    /// <summary>Far above any real sequence of attempts, and far below overflow.</summary>
    public const int MaxFailedAttempts = 1_000_000;

    /// <summary>
    /// Absorbs the difference between the caller's clock and the service's.
    ///
    /// Both are the same machine clock, so this is about the few milliseconds
    /// between a value being computed and being checked, not about time
    /// zones.
    /// </summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------ parsing

    /// <summary>
    /// Reads a document the privileged side already holds.
    ///
    /// Returns null for anything unreadable, and the callers treat null as
    /// "there is no previous value" only where that is safe - which, for the
    /// documents here, means the range checks still apply.
    /// </summary>
    public static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Matches how both documents are written by their own stores.
    ///
    /// Camel case, and nothing else configured: the privileged side must read
    /// these back exactly as the unprivileged side wrote them, so a second
    /// opinion about naming here would be a silent mismatch that makes every
    /// previous value look absent - and an absent previous value is the one
    /// case where the monotonic rules have nothing to compare against.
    /// </summary>
    public static readonly JsonSerializerOptions DocumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static bool IsLocalDateKey(string? value) =>
        value is { Length: 10 } &&
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private static BrokerDecision Allow(string why) => BrokerDecision.Allow(why);

    private static BrokerDecision Deny(string why) =>
        BrokerDecision.Deny(BrokerFailureReason.TransitionRejected, why);
}
