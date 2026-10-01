using System.Text.Json;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security;
using KidShell.Core.Security.Broker;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// THE RULES THAT MAKE A CHILD-WRITABLE ENFORCEMENT DOCUMENT SAFE.
///
/// Routing the screen-time counter through a LocalSystem service achieves
/// precisely nothing on its own: the child's process would ask SYSTEM to
/// write UsedSeconds = 0 and SYSTEM would oblige, and the protected store
/// would be protecting a number the child chose. Same for the throttle, where
/// the interesting value to write is FailedAttemptCount = 0.
///
/// So the authority that may write these may only write them in one
/// direction. Every test here has the same shape: stricter is accepted,
/// looser is refused, and the refusal is the security property.
/// </summary>
public class ProtectedStateTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 19, 0, 0, TimeSpan.Zero);

    private const BrokerAuthority AsChild = BrokerAuthority.ChildSessionRestricted;
    private const BrokerAuthority AsParent = BrokerAuthority.ParentCapability;

    private static ScreenTimeState Counter(
        int used = 600, int sequence = 5, string day = "2026-09-30",
        int bonus = 0, bool unlimited = false, int clockEvents = 0,
        ScreenTimeSessionState session = ScreenTimeSessionState.Open) => new()
    {
        LocalDate = day,
        UsedSeconds = used,
        Sequence = sequence,
        BonusMinutes = bonus,
        UnlimitedForToday = unlimited,
        SuspiciousClockEvents = clockEvents,
        SessionState = session
    };

    // ------------------------------------------------------- screen time

    [Fact]
    public void An_ordinary_tick_is_accepted()
    {
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(used: 600, sequence: 5),
            Counter(used: 630, sequence: 6),
            AsChild);

        Assert.True(decision.Allowed, decision.Explanation);
    }

    [Fact]
    public void Used_time_going_down_within_a_day_is_refused()
    {
        // The write a modified KidShell.App would send first.
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(used: 3600, sequence: 20),
            Counter(used: 0, sequence: 21),
            AsChild);

        Assert.False(decision.Allowed);
        Assert.Equal(BrokerFailureReason.TransitionRejected, decision.Reason);
    }

    [Fact]
    public void Used_time_going_down_is_refused_from_a_parent_capability_too()
    {
        // A grant is a different operation, where the privileged side
        // computes the result. Lowering the counter through an ordinary save
        // is refused whoever is asking, because the shape of the request is
        // "here is the state I would like", and nobody gets to send that.
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(used: 3600, sequence: 20),
            Counter(used: 0, sequence: 21),
            AsParent);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_sequence_that_goes_backwards_is_refused()
    {
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(sequence: 20), Counter(sequence: 19), AsChild);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_sequence_that_jumps_to_the_ceiling_is_refused()
    {
        // Forward-only is not enough. A caller that sets the sequence to
        // int.MaxValue once makes every honest write afterwards look like a
        // rollback, which is a denial of service against the parent.
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(sequence: 5),
            Counter(sequence: int.MaxValue),
            AsChild);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_child_cannot_grant_itself_bonus_minutes()
    {
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(bonus: 0), Counter(bonus: 120, sequence: 6), AsChild);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_child_cannot_lift_todays_limit()
    {
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(unlimited: false),
            Counter(unlimited: true, sequence: 6),
            AsChild);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_child_may_give_back_a_grant_it_was_given()
    {
        // Downwards on a bonus is the stricter direction, so there is no
        // reason to refuse it - and refusing would mean the counter could
        // never be written again after a grant expired.
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(bonus: 30), Counter(bonus: 0, sequence: 6), AsChild);

        Assert.True(decision.Allowed, decision.Explanation);
    }

    [Fact]
    public void Clock_evidence_cannot_be_erased()
    {
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(clockEvents: 3), Counter(clockEvents: 0, sequence: 6), AsChild);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_new_day_resets_the_counter_and_not_the_grants()
    {
        var yesterday = Counter(used: 3600, sequence: 40, day: "2026-09-29", bonus: 30, unlimited: true);

        Assert.True(ProtectedStateTransitionRules.ScreenTime(
            yesterday,
            Counter(used: 0, sequence: 41, day: "2026-09-30"),
            AsChild).Allowed);

        // Carrying the grants over would make "wait until tomorrow" a way to
        // keep an unlimited day forever.
        Assert.False(ProtectedStateTransitionRules.ScreenTime(
            yesterday,
            Counter(used: 0, sequence: 41, day: "2026-09-30", unlimited: true),
            AsChild).Allowed);

        Assert.False(ProtectedStateTransitionRules.ScreenTime(
            yesterday,
            Counter(used: 0, sequence: 41, day: "2026-09-30", bonus: 30),
            AsChild).Allowed);
    }

    [Fact]
    public void A_day_moving_backwards_is_refused()
    {
        // The engine refuses to roll over backwards, so reaching here means
        // something other than the engine wrote the file.
        var decision = ProtectedStateTransitionRules.ScreenTime(
            Counter(day: "2026-09-30", sequence: 40),
            Counter(day: "2026-09-29", sequence: 41, used: 0),
            AsChild);

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-day")]
    [InlineData("2026-13-01")]
    [InlineData("2026-09-31")]
    public void A_day_that_is_not_a_day_is_refused(string day)
    {
        Assert.False(ProtectedStateTransitionRules.ScreenTime(
            null, Counter(day: day), AsChild).Allowed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    [InlineData(int.MaxValue)]
    public void Used_seconds_outside_one_day_are_refused(int used)
    {
        Assert.False(ProtectedStateTransitionRules.ScreenTime(
            null, Counter(used: used), AsChild).Allowed);
    }

    [Fact]
    public void A_future_schema_is_refused()
    {
        var proposed = Counter();
        proposed.SchemaVersion = ScreenTimeState.CurrentSchemaVersion + 1;

        Assert.False(ProtectedStateTransitionRules.ScreenTime(Counter(), proposed, AsChild).Allowed);
    }

    [Fact]
    public void A_session_state_outside_the_enum_is_refused()
    {
        var proposed = Counter(session: (ScreenTimeSessionState)99);

        Assert.False(ProtectedStateTransitionRules.ScreenTime(Counter(), proposed, AsChild).Allowed);
    }

    [Fact]
    public void A_first_write_has_nothing_to_weaken()
    {
        Assert.True(ProtectedStateTransitionRules.ScreenTime(null, Counter(), AsChild).Allowed);
    }

    [Fact]
    public void A_counter_that_is_not_a_counter_is_refused()
    {
        Assert.False(ProtectedStateTransitionRules.ScreenTime(Counter(), null, AsChild).Allowed);
    }

    // ------------------------------------------------------ pin throttle

    private static PinThrottleState Throttle(int failures, DateTimeOffset? until, DateTimeOffset? last = null) =>
        new() { FailedAttemptCount = failures, CooldownUntilUtc = until, LastFailureUtc = last };

    [Fact]
    public void One_more_failure_is_accepted()
    {
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(2, Now.AddSeconds(5)),
            Throttle(3, Now.AddSeconds(15)),
            AsChild, Now);

        Assert.True(decision.Allowed, decision.Explanation);
    }

    [Fact]
    public void A_child_cannot_forgive_its_own_failures()
    {
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(5, Now.AddSeconds(60)),
            Throttle(0, null),
            AsChild, Now);

        Assert.False(decision.Allowed);
        Assert.Equal(BrokerFailureReason.TransitionRejected, decision.Reason);
    }

    [Fact]
    public void A_child_cannot_shorten_an_active_cooldown()
    {
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(5, Now.AddSeconds(60)),
            Throttle(5, Now.AddSeconds(1)),
            AsChild, Now);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_child_cannot_clear_an_active_cooldown_while_keeping_the_count()
    {
        // The subtler shape of the same attack: the count looks untouched,
        // and the thing that costs time is gone.
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(5, Now.AddSeconds(60)),
            Throttle(5, null),
            AsChild, Now);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void An_expired_cooldown_may_be_left_behind()
    {
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(5, Now.AddSeconds(-1)),
            Throttle(5, null),
            AsChild, Now);

        Assert.True(decision.Allowed, decision.Explanation);
    }

    [Fact]
    public void A_cooldown_beyond_the_policy_maximum_is_refused()
    {
        // In the other direction, and for the opposite reason: a cooldown in
        // the year 3000 locks a parent out of their own computer, which is
        // worse than a few free attempts.
        var decision = ProtectedStateTransitionRules.PinThrottle(
            null,
            Throttle(1, new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            AsChild, Now);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_parent_capability_may_clear_the_throttle()
    {
        // This is what "the parent got in" means, and it is why clearing is
        // not simply forbidden.
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(5, Now.AddSeconds(60)),
            Throttle(0, null),
            AsParent, Now);

        Assert.True(decision.Allowed, decision.Explanation);
    }

    [Fact]
    public void The_last_failure_cannot_move_backwards()
    {
        var decision = ProtectedStateTransitionRules.PinThrottle(
            Throttle(3, Now.AddSeconds(30), Now),
            Throttle(4, Now.AddSeconds(60), Now.AddHours(-2)),
            AsChild, Now);

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_failure_count_out_of_range_is_refused(int count)
    {
        Assert.False(ProtectedStateTransitionRules.PinThrottle(
            null, Throttle(count, null), AsChild, Now).Allowed);
    }

    [Fact]
    public void A_throttle_in_a_future_schema_is_refused()
    {
        var proposed = Throttle(1, null);
        proposed.SchemaVersion = PinThrottleState.CurrentSchemaVersion + 1;

        Assert.False(ProtectedStateTransitionRules.PinThrottle(null, proposed, AsChild, Now).Allowed);
    }

    // --------------------------------------------------------- parsing

    [Fact]
    public void The_rules_read_documents_exactly_as_their_stores_write_them()
    {
        // A naming mismatch here would make every previous value look
        // absent, and an absent previous value is the one case where the
        // monotonic rules have nothing to compare against - so the refusals
        // above would all quietly become allowances.
        var written = JsonSerializer.Serialize(
            Counter(used: 1234, sequence: 7),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var read = ProtectedStateTransitionRules.Parse<ScreenTimeState>(written);

        Assert.NotNull(read);
        Assert.Equal(1234, read.UsedSeconds);
        Assert.Equal(7, read.Sequence);
        Assert.Equal("2026-09-30", read.LocalDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void An_unreadable_document_parses_to_nothing(string? json) =>
        Assert.Null(ProtectedStateTransitionRules.Parse<ScreenTimeState>(json));
}
