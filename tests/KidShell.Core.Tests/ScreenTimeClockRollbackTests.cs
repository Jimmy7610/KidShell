using System.Globalization;
using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// EXTERNAL AUDIT FINDING 05 — winding the clock back past midnight bought a
/// fresh allowance.
///
/// THE BUG
/// -------
/// The day rollover asked only whether the stored local date DIFFERED from
/// today's, and any difference started a new day. A clock moved backwards over
/// midnight differs, so it read as an ordinary new morning and the counter
/// went to zero.
///
/// It was self-concealing too. The reset wrote a fresh LastUpdatedUtc, and the
/// tamper check - which ran immediately afterwards and compares "now" against
/// that very field - then had nothing left to notice. So the counter reset and
/// the parent was told nothing.
///
/// WHAT DECIDES IT NOW
/// -------------------
/// The direction of the change, not merely that there was one:
///
///     today is LATER than stored   -> an ordinary new day, counter resets
///     today is EARLIER than stored -> the clock went backwards, counter stays
///     today is the same            -> nothing to do
///
/// and the tamper check now runs FIRST, so it reads the timestamp the previous
/// session actually wrote rather than one the rollover just replaced.
///
/// DAYLIGHT SAVING IS NOT TAMPERING
/// --------------------------------
/// Neither rule is disturbed by the clocks changing. Tamper detection compares
/// UTC, which never moves backwards, and rollover compares local calendar
/// dates, which a one-hour autumn shift at 03:00 does not alter.
/// </summary>

/// <summary>
/// Time zones the tests need, or null where this machine has no database for
/// them. A missing zone skips the assertion rather than failing it: the point
/// is the engine's arithmetic, not the host's tzdata.
/// </summary>
internal static class TestTimeZones
{
    public static TimeZoneInfo? Stockholm => Find("W. Europe Standard Time", "Europe/Stockholm");

    private static TimeZoneInfo? Find(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next spelling.
            }
            catch (InvalidTimeZoneException)
            {
                // Corrupt entry; treat as absent.
            }
        }

        return null;
    }
}

public class ScreenTimeClockRollbackTests
{
    private static (ScreenTimeEngine Engine, InMemoryScreenTimeStateStore Store, FakeTimeProvider Time) Create(
        TempDirectory dir,
        DateTimeOffset start,
        TimeZoneInfo? zone = null,
        InMemoryScreenTimeStateStore? store = null)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 60;
        draft.ScreenTime.WeekendMinutes = 120;
        state.Commit(draft);

        var time = new FakeTimeProvider(start, zone);
        store ??= new InMemoryScreenTimeStateStore();

        return (new ScreenTimeEngine(state, store, logger, time), store, time);
    }

    /// <summary>
    /// Spends real time, the way a session does.
    ///
    /// One big jump would be credited as nothing: a gap longer than
    /// MaximumCreditedTick is read as the machine having been asleep, which is
    /// correct behaviour and not what these tests are about. So time passes in
    /// four-minute steps with a tick after each, as a running session does.
    /// </summary>
    private static void Spend(ScreenTimeEngine engine, FakeTimeProvider time, int minutes)
    {
        for (var spent = 0; spent < minutes; spent += 4)
        {
            time.Advance(TimeSpan.FromMinutes(Math.Min(4, minutes - spent)));
            engine.Tick();
        }
    }

    // ------------------------------------------------ the reported bug

    /// <summary>
    /// The audit's scenario exactly: a day's allowance spent, then the clock
    /// wound back into the previous day.
    /// </summary>
    [Fact]
    public void Winding_the_clock_back_over_midnight_does_not_return_the_allowance()
    {
        using var dir = new TempDirectory();

        // Thursday 2026-09-24, late evening.
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 60);

        Assert.Equal(ScreenTimeStatus.Expired, engine.Evaluate().Status);
        Assert.Equal(TimeSpan.FromMinutes(60), engine.Evaluate().Used);

        // Somebody sets the clock back into yesterday.
        time.SetWallClock(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(60), snapshot.Used);
        Assert.Equal(ScreenTimeStatus.Expired, snapshot.Status);
        Assert.True(engine.State.SuspiciousClockEvents > 0, "the clock change was not recorded");
    }

    [Fact]
    public void Winding_the_clock_back_over_midnight_still_refuses_a_launch()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 60);

        time.SetWallClock(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
        engine.Tick();

        Assert.Equal(ScreenTimeStatus.Expired, engine.Evaluate().Status);
    }

    /// <summary>
    /// Days rather than hours back. A bigger lie is not a better one.
    /// </summary>
    [Fact]
    public void Winding_the_clock_back_a_week_does_not_return_the_allowance()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 60);

        time.SetWallClock(new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(60), snapshot.Used);
        Assert.Equal(ScreenTimeStatus.Expired, snapshot.Status);
    }

    /// <summary>
    /// Backwards but within the same day: no rollover was ever involved, and
    /// this already worked. Kept so a future change cannot quietly break it.
    /// </summary>
    [Fact]
    public void Winding_the_clock_back_within_the_same_day_does_not_return_the_allowance()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 60);

        time.SetWallClock(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(60), snapshot.Used);
        Assert.True(engine.State.SuspiciousClockEvents > 0);
    }

    // ------------------------------------------------ legitimate transitions

    [Fact]
    public void An_ordinary_new_day_does_return_the_allowance()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 60);
        Assert.Equal(ScreenTimeStatus.Expired, engine.Evaluate().Status);

        // The next morning.
        time.SetWallClock(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.Zero, snapshot.Used);
        Assert.Equal(ScreenTimeStatus.Running, snapshot.Status);
    }

    [Fact]
    public void A_new_day_is_not_recorded_as_a_clock_change()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 30);

        time.SetWallClock(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
        engine.Tick();

        Assert.Equal(0, engine.State.SuspiciousClockEvents);
    }

    [Fact]
    public void Skipping_several_days_returns_the_allowance_once()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 45);

        // The machine was off for a long weekend.
        time.SetWallClock(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.Zero, snapshot.Used);
        Assert.Equal(0, engine.State.SuspiciousClockEvents);
    }

    /// <summary>
    /// A month and a year boundary, where string comparison of yyyy-MM-dd has
    /// to still mean what date comparison means.
    /// </summary>
    [Theory]
    [InlineData("2026-09-30T22:00:00Z", "2026-10-01T08:00:00Z", true)]   // month forward
    [InlineData("2026-10-01T08:00:00Z", "2026-09-30T22:00:00Z", false)]  // month backward
    [InlineData("2026-12-31T22:00:00Z", "2027-01-01T08:00:00Z", true)]   // year forward
    [InlineData("2027-01-01T08:00:00Z", "2026-12-31T22:00:00Z", false)]  // year backward
    public void Only_a_later_date_returns_the_allowance(string from, string to, bool expectReset)
    {
        using var dir = new TempDirectory();
        // Invariant, explicitly. The product's contract here is a fixed ISO-8601
        // instant, not "whatever this host's locale reads that as" - and a
        // test host in a culture with a different date order parsed these into
        // different days, which is a deviation in the test rather than in the
        // product.
        var (engine, _, time) = Create(dir, DateTimeOffset.Parse(from, CultureInfo.InvariantCulture));

        Spend(engine, time, 60);

        time.SetWallClock(DateTimeOffset.Parse(to, CultureInfo.InvariantCulture));
        var snapshot = engine.Tick();

        Assert.Equal(expectReset ? TimeSpan.Zero : TimeSpan.FromMinutes(60), snapshot.Used);
    }

    // ------------------------------------------------ daylight saving

    /// <summary>
    /// The clocks going back in the autumn. Local wall time repeats an hour,
    /// the calendar date does not change, and UTC does not move backwards - so
    /// neither the counter nor the tamper flag should react.
    /// </summary>
    [Fact]
    public void The_autumn_clock_change_is_not_treated_as_tampering()
    {
        using var dir = new TempDirectory();

        if (TestTimeZones.Stockholm is not { } stockholm)
        {
            return;     // no tz database on this host; nothing to assert
        }

        // 2026-10-25 02:00 UTC is inside the Swedish autumn change: local time
        // goes from 03:00 CEST back to 02:00 CET on the same date.
        var (engine, _, time) = Create(
            dir, new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), stockholm);

        Spend(engine, time, 20);

        var usedBefore = engine.Evaluate().Used;

        // UTC keeps moving forward across the boundary; only the local offset
        // changes. Nothing about that is a clock being wound back.
        time.Advance(TimeSpan.FromHours(2));
        var snapshot = engine.Tick();

        Assert.Equal(0, engine.State.SuspiciousClockEvents);
        Assert.True(snapshot.Used >= usedBefore, "usage went backwards across the DST boundary");
    }

    [Fact]
    public void The_spring_clock_change_is_not_treated_as_a_new_day()
    {
        using var dir = new TempDirectory();

        if (TestTimeZones.Stockholm is not { } stockholm)
        {
            return;     // no tz database on this host
        }

        // 2026-03-29 01:00 UTC: local jumps 02:00 -> 03:00, same date.
        var (engine, _, time) = Create(
            dir, new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), stockholm);

        Spend(engine, time, 40);
        var used = engine.Evaluate().Used;

        time.Advance(TimeSpan.FromHours(1));
        var snapshot = engine.Tick();

        Assert.True(snapshot.Used >= used);
        Assert.Equal(0, engine.State.SuspiciousClockEvents);
    }

    // ------------------------------------------------ restart around midnight

    /// <summary>
    /// A restart is a fresh engine reading the same stored state. The
    /// constructor rolls over too, so it has to make the same distinction the
    /// tick does.
    /// </summary>
    [Fact]
    public void A_restart_just_before_midnight_keeps_the_days_usage()
    {
        using var dir = new TempDirectory();
        var store = new InMemoryScreenTimeStateStore();

        var (first, _, time) = Create(
            dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero), store: store);

        Spend(first, time, 30);
        Assert.Equal(TimeSpan.FromMinutes(30), first.Evaluate().Used);

        // An ordinary shutdown. Without it the next start sees an unclosed
        // session and fails closed, which is right for a crash and wrong for
        // a restart.
        first.CloseSession();
        // Restarted later the same evening, still before midnight.
        var (second, _, _) = Create(
            dir, new DateTimeOffset(2026, 9, 24, 23, 55, 0, TimeSpan.Zero), store: store);

        Assert.Equal(TimeSpan.FromMinutes(30), second.Evaluate().Used);
    }

    [Fact]
    public void A_restart_just_after_midnight_starts_the_day_again()
    {
        using var dir = new TempDirectory();
        var store = new InMemoryScreenTimeStateStore();

        var (first, _, time) = Create(
            dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero), store: store);

        Spend(first, time, 30);

        // An ordinary shutdown. Without it the next start sees an unclosed
        // session and fails closed, which is right for a crash and wrong for
        // a restart.
        first.CloseSession();
        // Restarted after midnight.
        var (second, _, _) = Create(
            dir, new DateTimeOffset(2026, 9, 25, 0, 10, 0, TimeSpan.Zero), store: store);

        Assert.Equal(TimeSpan.Zero, second.Evaluate().Used);
    }

    /// <summary>
    /// The same restart, but the clock was wound back first. Starting the
    /// process again must not be a way to launder a rolled-back clock.
    /// </summary>
    [Fact]
    public void A_restart_with_the_clock_wound_back_keeps_the_usage()
    {
        using var dir = new TempDirectory();
        var store = new InMemoryScreenTimeStateStore();

        var (first, _, time) = Create(
            dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero), store: store);

        Spend(first, time, 60);
        Assert.Equal(ScreenTimeStatus.Expired, first.Evaluate().Status);

        // An ordinary shutdown. Without it the next start sees an unclosed
        // session and fails closed, which is right for a crash and wrong for
        // a restart.
        first.CloseSession();
        // Clock wound back, then KidShell restarted.
        var (second, _, _) = Create(
            dir, new DateTimeOffset(2026, 9, 23, 18, 0, 0, TimeSpan.Zero), store: store);

        Assert.Equal(TimeSpan.FromMinutes(60), second.Evaluate().Used);
        Assert.Equal(ScreenTimeStatus.Expired, second.Evaluate().Status);
    }

    // ------------------------------------------------ parent override

    /// <summary>
    /// A parent granting time must still work after a clock change: the point
    /// is to refuse the child a reset, not to lock the parent out of their own
    /// decision.
    /// </summary>
    [Fact]
    public void A_parent_can_still_grant_time_after_a_clock_change()
    {
        using var dir = new TempDirectory();
        var (engine, _, time) = Create(dir, new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

        Spend(engine, time, 60);

        time.SetWallClock(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
        engine.Tick();
        Assert.Equal(ScreenTimeStatus.Expired, engine.Evaluate().Status);

        engine.GrantExtension(15);

        Assert.NotEqual(ScreenTimeStatus.Expired, engine.Evaluate().Status);
    }
}
