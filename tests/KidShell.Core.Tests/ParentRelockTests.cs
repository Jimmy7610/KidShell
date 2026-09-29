using KidShell.Core.Runtime;
using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV RETEST 2, FINDING 04 — Parent Mode did not re-lock when screen time
/// was disabled or already expired.
///
/// The timeout was evaluated from `ScreenTime.Changed`, which fires when the
/// remaining minutes change. With screen time off, unlimited, or already run
/// out, that event never fires — and those are precisely the states in which
/// Parent Mode stayed open indefinitely. OPSV measured twenty simulated
/// minutes with the session still unlocked.
///
/// A session lifetime cannot depend on a signal that is allowed to be silent.
/// </summary>
public class ParentRelockTests
{
    private static (ParentSession Session, FakeTimeProvider Time, ManualPeriodicScheduler Scheduler) Build()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 19, 0, 0, TimeSpan.Zero));
        var session = new ParentSession(new RecordingLogger(), time);
        var scheduler = new ManualPeriodicScheduler();

        // Wired exactly as the shell wires it.
        scheduler.Start(ParentSession.HeartbeatInterval, session.Evaluate);

        return (session, time, scheduler);
    }

    /// <summary>Moves both the clock and the heartbeat, the way real time does.</summary>
    private static void Elapse(FakeTimeProvider time, ManualPeriodicScheduler scheduler, TimeSpan span)
    {
        var beats = (int)(span.Ticks / ParentSession.HeartbeatInterval.Ticks);

        for (var i = 0; i < beats; i++)
        {
            time.Advance(ParentSession.HeartbeatInterval);
            scheduler.Advance(ParentSession.HeartbeatInterval);
        }
    }

    // ------------------------------------------- the reported reproduction

    [Fact]
    public void Twenty_idle_minutes_lock_the_session_with_no_screen_time_activity_at_all()
    {
        // The exact OPSV case: nothing about screen time changes, because in
        // this test there is no screen time. The session must still expire.
        var (session, time, scheduler) = Build();

        session.Begin();
        Assert.True(session.IsUnlocked);

        Elapse(time, scheduler, TimeSpan.FromMinutes(20));

        Assert.False(session.IsUnlocked);
        Assert.Equal(ParentSessionEndReason.Inactivity, session.LastEndReason);
    }

    [Fact]
    public void The_heartbeat_is_what_expires_it_not_a_property_read()
    {
        // Proving the mechanism rather than the outcome. If only the
        // IsUnlocked getter expired the session, a shell that never read the
        // property would keep Parent Mode open forever - which is close to
        // what the old code did.
        var (session, time, scheduler) = Build();

        var ended = 0;
        session.Ended += (_, _) => ended++;

        session.Begin();

        Elapse(time, scheduler, TimeSpan.FromMinutes(20));

        Assert.Equal(1, ended);
    }

    [Fact]
    public void Activity_at_minute_ten_holds_the_session_until_twenty_five()
    {
        var (session, time, scheduler) = Build();

        session.Begin();

        Elapse(time, scheduler, TimeSpan.FromMinutes(10));
        session.Touch();

        Elapse(time, scheduler, TimeSpan.FromMinutes(14));
        Assert.True(session.IsUnlocked);

        Elapse(time, scheduler, TimeSpan.FromMinutes(2));
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void A_heartbeat_is_not_activity()
    {
        // The trap this design invites: if the tick counted as parent
        // activity, the session would never expire at all.
        var (session, time, scheduler) = Build();

        session.Begin();
        Elapse(time, scheduler, TimeSpan.FromMinutes(20));

        Assert.True(scheduler.FireCount > 0, "the heartbeat never fired");
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public void The_timeout_does_not_fire_twice()
    {
        var (session, time, scheduler) = Build();

        var ended = 0;
        session.Ended += (_, _) => ended++;

        session.Begin();
        Elapse(time, scheduler, TimeSpan.FromMinutes(40));

        Assert.Equal(1, ended);
    }

    [Fact]
    public void Returning_to_child_mode_locks_immediately()
    {
        var (session, _, _) = Build();

        session.Begin();
        session.End(ParentSessionEndReason.ReturnedToChild);

        Assert.False(session.IsUnlocked);
        Assert.Equal(ParentSessionEndReason.ReturnedToChild, session.LastEndReason);
    }

    [Fact]
    public void Disposing_the_scheduler_stops_the_callbacks()
    {
        var (session, time, scheduler) = Build();

        session.Begin();
        scheduler.Dispose();

        time.Advance(TimeSpan.FromMinutes(20));
        scheduler.Advance(TimeSpan.FromMinutes(20));

        // The session has not been asked, so it has not ended. The clock
        // getter would still answer honestly, which is why this asserts on the
        // scheduler rather than on IsUnlocked.
        Assert.Equal(0, scheduler.FireCount);
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public void Starting_twice_does_not_leave_two_timers_running()
    {
        using var scheduler = new TimerPeriodicScheduler();

        var fired = 0;
        scheduler.Start(TimeSpan.FromMilliseconds(30), () => Interlocked.Increment(ref fired));
        scheduler.Start(TimeSpan.FromMilliseconds(30), () => Interlocked.Increment(ref fired));
        scheduler.Stop();

        var afterStop = fired;
        Thread.Sleep(120);

        Assert.Equal(afterStop, fired);
    }

    [Fact]
    public void A_callback_that_throws_does_not_take_the_process_down()
    {
        using var scheduler = new TimerPeriodicScheduler();

        scheduler.Start(TimeSpan.FromMilliseconds(20), () => throw new InvalidOperationException("boom"));
        Thread.Sleep(100);

        // Reaching here at all is the assertion: an unhandled exception on a
        // timer thread terminates the process.
        Assert.True(true);
    }
}
