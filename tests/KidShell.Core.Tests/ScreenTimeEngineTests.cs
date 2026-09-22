using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// A TimeProvider the tests drive by hand.
///
/// Both clocks move together but are asked separately: the monotonic
/// timestamp is what credits usage, and the wall clock is what decides which
/// day it is. Several tests move only one of them, which is exactly how a
/// clock change or a DST boundary looks to the engine.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public FakeTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null)
    {
        _utcNow = start;
        Zone = zone ?? TimeZoneInfo.Utc;
    }

    public TimeZoneInfo Zone { get; set; }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Moves both clocks forward, as real time passing does.</summary>
    public void Advance(TimeSpan by)
    {
        _utcNow = _utcNow.Add(by);
        _timestamp += by.Ticks;
    }

    /// <summary>
    /// Moves only the monotonic clock. What the engine sees when the wall
    /// clock is held still but time really passes.
    /// </summary>
    public void AdvanceMonotonicOnly(TimeSpan by) => _timestamp += by.Ticks;

    /// <summary>Moves only the wall clock. What a clock change looks like.</summary>
    public void SetWallClock(DateTimeOffset utcNow) => _utcNow = utcNow;
}

public class ScreenTimeEngineTests
{
    private static (ScreenTimeEngine Engine, AppStateService State, InMemoryScreenTimeStateStore Store, FakeTimeProvider Time)
        Create(
            TempDirectory dir,
            Action<ScreenTimeSettings>? configure = null,
            DateTimeOffset? start = null,
            TimeZoneInfo? zone = null)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 60;
        draft.ScreenTime.WeekendMinutes = 120;
        configure?.Invoke(draft.ScreenTime);
        state.Commit(draft);

        // A Wednesday, so weekday rules apply unless a test says otherwise.
        var time = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero), zone);
        var store = new InMemoryScreenTimeStateStore();

        return (new ScreenTimeEngine(state, store, logger, time), state, store, time);
    }

    // ------------------------------------------------ basic accounting

    [Fact]
    public void Time_is_credited_as_it_passes()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(TimeSpan.FromMinutes(2));
        engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(2), engine.Evaluate().Used);
        Assert.Equal(TimeSpan.FromMinutes(58), engine.Evaluate().Remaining);
    }

    [Fact]
    public void A_disabled_limit_counts_nothing()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s => s.IsEnabled = false);

        time.Advance(TimeSpan.FromMinutes(30));
        var snapshot = engine.Tick();

        Assert.Equal(ScreenTimeStatus.NotLimited, snapshot.Status);
        Assert.Equal(TimeSpan.Zero, snapshot.Used);
    }

    [Fact]
    public void The_weekend_allowance_applies_at_the_weekend()
    {
        using var dir = new TempDirectory();

        // A Saturday.
        var (engine, _, _, _) = Create(dir, start: new DateTimeOffset(2026, 3, 14, 10, 0, 0, TimeSpan.Zero));

        var snapshot = engine.Evaluate();

        Assert.True(snapshot.IsWeekend);
        Assert.Equal(TimeSpan.FromMinutes(120), snapshot.Allowance);
    }

    [Fact]
    public void Running_out_blocks()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s => s.WeekdayMinutes = 10);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        var snapshot = engine.Tick();

        Assert.Equal(ScreenTimeStatus.Expired, snapshot.Status);
        Assert.True(snapshot.IsBlocked);
        Assert.Equal(TimeSpan.Zero, snapshot.Remaining);
    }

    [Fact]
    public void Remaining_never_goes_negative()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s => s.WeekdayMinutes = 5);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(4));
            engine.Tick();
        }

        // "Minus twelve minutes" is not a useful thing to show a child.
        Assert.Equal(TimeSpan.Zero, engine.Evaluate().Remaining);
    }

    // ------------------------------------------------ warnings

    [Fact]
    public void Warnings_fire_at_the_configured_thresholds()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s =>
        {
            s.WeekdayMinutes = 20;
            s.WarningMinutes = [15, 5, 1];
        });

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        Assert.Equal(ScreenTimeStatus.Running, engine.Evaluate().Status);

        // 16 used, 4 remaining -> the 5-minute warning.
        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(16), snapshot.Used);
        Assert.Equal(ScreenTimeStatus.Warning, snapshot.Status);
        Assert.Equal(5, snapshot.WarningMinutes);
    }

    [Fact]
    public void The_tightest_applicable_threshold_is_reported()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s =>
        {
            s.WeekdayMinutes = 20;
            s.WarningMinutes = [15, 5, 1];
        });

        // 4 used, 16 remaining: nothing crossed yet.
        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        Assert.Null(engine.Evaluate().WarningMinutes);

        // 8 used, 12 remaining: inside 15 but not yet inside 5, so 15 is the
        // tightest one that applies.
        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        Assert.Equal(15, engine.Evaluate().WarningMinutes);
    }

    [Fact]
    public void A_status_change_raises_the_event_once()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s =>
        {
            s.WeekdayMinutes = 10;
            s.WarningMinutes = [5];
        });

        var raised = 0;
        engine.StatusChanged += (_, _) => raised++;

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        Assert.Equal(0, raised);

        // Crosses into the warning.
        time.Advance(TimeSpan.FromMinutes(2));
        engine.Tick();
        Assert.Equal(1, raised);

        // Still in the warning: no second event.
        time.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();
        Assert.Equal(1, raised);
    }

    // ------------------------------------------------ restarts and sleep

    [Fact]
    public void Usage_survives_a_restart()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 60;
        state.Commit(draft);

        var store = new InMemoryScreenTimeStateStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));

        var first = new ScreenTimeEngine(state, store, logger, time);
        time.Advance(TimeSpan.FromMinutes(4));
        first.Tick();

        // Restarting KidShell must not hand the child a fresh hour.
        var second = new ScreenTimeEngine(state, store, logger, time);

        Assert.Equal(TimeSpan.FromMinutes(4), second.Evaluate().Used);
    }

    [Fact]
    public void Sleeping_overnight_does_not_consume_the_allowance()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        // Closing the lid for eight hours is not eight hours of use.
        time.AdvanceMonotonicOnly(TimeSpan.FromHours(8));
        var snapshot = engine.Tick();

        Assert.Equal(TimeSpan.Zero, snapshot.Used);
    }

    [Fact]
    public void A_tick_at_the_credit_limit_still_counts()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(ScreenTimeEngine.MaximumCreditedTick);
        engine.Tick();

        Assert.Equal(ScreenTimeEngine.MaximumCreditedTick, engine.Evaluate().Used);
    }

    [Fact]
    public void State_is_written_on_every_tick_that_changes_it()
    {
        using var dir = new TempDirectory();
        var (engine, _, store, time) = Create(dir);

        var before = store.SaveCount;

        time.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        // A crash should cost one tick, not a session.
        Assert.True(store.SaveCount > before);
    }

    // ------------------------------------------------ day boundary

    [Fact]
    public void A_new_day_starts_the_counter_again()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        Assert.Equal(TimeSpan.FromMinutes(4), engine.Evaluate().Used);

        // Next day.
        time.SetWallClock(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero));
        engine.Tick();

        Assert.Equal(TimeSpan.Zero, engine.Evaluate().Used);
    }

    [Fact]
    public void A_bonus_does_not_survive_the_day()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        engine.GrantExtension(30);
        Assert.Equal(TimeSpan.FromMinutes(90), engine.Evaluate().Allowance);

        time.SetWallClock(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero));
        engine.Tick();

        Assert.Equal(TimeSpan.FromMinutes(60), engine.Evaluate().Allowance);
    }

    [Fact]
    public void The_day_boundary_survives_the_spring_clock_change()
    {
        // 29 March 2026 is when CET moves to CEST: that local day is 23 hours
        // long. Anything computing "midnight plus 24 hours" is wrong here.
        var stockholm = TryFindZone("W. Europe Standard Time", "Europe/Stockholm");

        if (stockholm is null)
        {
            return;     // no tz database on this machine; nothing to assert
        }

        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(
            dir,
            start: new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero),
            zone: stockholm);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        var usedBefore = engine.Evaluate().Used;

        // Move to later the same local day, across the transition.
        time.SetWallClock(new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.Zero));
        engine.Tick();

        // Same local date, so the counter must not have reset.
        Assert.True(engine.Evaluate().Used >= usedBefore);
    }

    [Fact]
    public void The_local_date_key_is_what_identifies_a_day()
    {
        var key = ScreenTimeEngine.LocalDateKey(new DateTimeOffset(2026, 3, 29, 14, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal("2026-03-29", key);
    }

    private static TimeZoneInfo? TryFindZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // Try the next spelling.
            }
        }

        return null;
    }

    // ------------------------------------------------ extensions

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void A_parent_can_grant_extra_minutes(int minutes)
    {
        using var dir = new TempDirectory();
        var (engine, _, _, _) = Create(dir);

        var snapshot = engine.GrantExtension(minutes);

        Assert.Equal(TimeSpan.FromMinutes(60 + minutes), snapshot.Allowance);
        Assert.True(snapshot.HasBonus);
    }

    [Fact]
    public void Extensions_accumulate()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, _) = Create(dir);

        engine.GrantExtension(15);
        engine.GrantExtension(15);

        Assert.Equal(TimeSpan.FromMinutes(90), engine.Evaluate().Allowance);
    }

    [Fact]
    public void An_extension_unblocks_an_expired_session()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s => s.WeekdayMinutes = 5);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(engine.Tick().IsBlocked);

        var snapshot = engine.GrantExtension(30);

        Assert.False(snapshot.IsBlocked);
    }

    [Fact]
    public void Rest_of_day_removes_the_limit_until_tomorrow()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir, s => s.WeekdayMinutes = 5);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();

        Assert.Equal(ScreenTimeStatus.NotLimited, engine.GrantRestOfDay().Status);

        time.SetWallClock(new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero));
        engine.Tick();

        Assert.NotEqual(ScreenTimeStatus.NotLimited, engine.Evaluate().Status);
    }

    [Fact]
    public void A_negative_extension_is_ignored()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, _) = Create(dir);

        engine.GrantExtension(-30);

        Assert.Equal(TimeSpan.FromMinutes(60), engine.Evaluate().Allowance);
    }

    [Fact]
    public void A_parent_can_reset_todays_counter()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();

        engine.ResetToday();

        Assert.Equal(TimeSpan.Zero, engine.Evaluate().Used);
    }

    // ------------------------------------------------ allowed hours

    [Theory]
    [InlineData(7, 20, 6, false)]
    [InlineData(7, 20, 7, true)]
    [InlineData(7, 20, 19, true)]
    [InlineData(7, 20, 20, false)]
    [InlineData(7, 20, 23, false)]
    public void Allowed_hours_are_respected(int from, int until, int hour, bool expected)
    {
        var settings = new ScreenTimeSettings { RestrictHours = true, AllowedFromHour = from, AllowedUntilHour = until };
        var now = new DateTimeOffset(2026, 3, 11, hour, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, ScreenTimeEngine.IsWithinAllowedHours(now, settings));
    }

    [Theory]
    [InlineData(21, 7, 22, true)]
    [InlineData(21, 7, 3, true)]
    [InlineData(21, 7, 12, false)]
    public void A_window_that_wraps_past_midnight_works(int from, int until, int hour, bool expected)
    {
        // A parent may well express "not during the night" this way round.
        var settings = new ScreenTimeSettings { RestrictHours = true, AllowedFromHour = from, AllowedUntilHour = until };
        var now = new DateTimeOffset(2026, 3, 11, hour, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, ScreenTimeEngine.IsWithinAllowedHours(now, settings));
    }

    [Fact]
    public void A_zero_width_window_does_not_block_everything()
    {
        // Setting both to the same hour was never meant to mean "never".
        var settings = new ScreenTimeSettings { RestrictHours = true, AllowedFromHour = 8, AllowedUntilHour = 8 };

        Assert.True(ScreenTimeEngine.IsWithinAllowedHours(
            new DateTimeOffset(2026, 3, 11, 15, 0, 0, TimeSpan.Zero), settings));
    }

    [Fact]
    public void Being_outside_allowed_hours_blocks_even_with_time_left()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, _) = Create(
            dir,
            s =>
            {
                s.RestrictHours = true;
                s.AllowedFromHour = 7;
                s.AllowedUntilHour = 20;
            },
            start: new DateTimeOffset(2026, 3, 11, 23, 0, 0, TimeSpan.Zero));

        var snapshot = engine.Evaluate();

        // An hour left at 23:00 still means bedtime.
        Assert.Equal(ScreenTimeStatus.OutsideAllowedHours, snapshot.Status);
        Assert.True(snapshot.IsBlocked);
        Assert.True(snapshot.Remaining > TimeSpan.Zero);
    }

    // ------------------------------------------------ clock tampering

    [Fact]
    public void Moving_the_clock_backwards_is_noticed_and_recorded()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();

        // Wind the wall clock back an hour.
        time.SetWallClock(time.GetUtcNow() - TimeSpan.FromHours(1));
        engine.Tick();

        Assert.True(engine.Evaluate().ClockLooksTampered);
        Assert.True(engine.State.SuspiciousClockEvents > 0);
    }

    [Fact]
    public void Winding_the_clock_back_does_not_refund_used_time()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(TimeSpan.FromMinutes(4));
        engine.Tick();
        var used = engine.Evaluate().Used;

        time.SetWallClock(time.GetUtcNow() - TimeSpan.FromHours(2));
        engine.Tick();

        // Usage comes from the monotonic clock, so the wall clock cannot
        // hand the child their afternoon back.
        Assert.True(engine.Evaluate().Used >= used);
    }

    [Fact]
    public void A_small_clock_correction_is_not_treated_as_tampering()
    {
        using var dir = new TempDirectory();
        var (engine, _, _, time) = Create(dir);

        time.Advance(TimeSpan.FromMinutes(2));
        engine.Tick();

        // NTP corrections of a few seconds are normal and not suspicious.
        time.SetWallClock(time.GetUtcNow() - TimeSpan.FromSeconds(20));
        engine.Tick();

        Assert.False(engine.Evaluate().ClockLooksTampered);
    }

    // ------------------------------------------------ persistence

    [Fact]
    public void A_corrupt_state_file_starts_a_fresh_counter()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        File.WriteAllText(path, "{ not json");

        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        // Losing today's count is an annoyance; failing to start is not.
        Assert.Equal(0, store.Load().UsedSeconds);
    }

    [Fact]
    public void A_future_schema_starts_a_fresh_counter()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        File.WriteAllText(path, """{ "schemaVersion": 99, "usedSeconds": 3600 }""");

        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        // A number from a schema we do not understand might not mean seconds.
        Assert.Equal(0, store.Load().UsedSeconds);
    }

    [Fact]
    public void Negative_persisted_values_are_clamped()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "usedSeconds": -500, "bonusMinutes": -10 }""");

        var state = new JsonScreenTimeStateStore(path, new RecordingLogger()).Load();

        Assert.Equal(0, state.UsedSeconds);
        Assert.Equal(0, state.BonusMinutes);
    }

    [Fact]
    public void State_round_trips_through_the_json_store()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        Assert.True(store.Save(new ScreenTimeState
        {
            LocalDate = "2026-03-11",
            UsedSeconds = 1234,
            BonusMinutes = 15,
            SuspiciousClockEvents = 2
        }));

        var loaded = store.Load();

        Assert.Equal("2026-03-11", loaded.LocalDate);
        Assert.Equal(1234, loaded.UsedSeconds);
        Assert.Equal(15, loaded.BonusMinutes);
        Assert.Equal(2, loaded.SuspiciousClockEvents);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = "2026-03-11" });
        store.Save(new ScreenTimeState { LocalDate = "2026-03-11", UsedSeconds = 60 });

        Assert.False(File.Exists(path + ".tmp"));
    }
}
