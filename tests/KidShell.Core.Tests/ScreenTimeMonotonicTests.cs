using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV RETEST 2, FINDING 03 — a write failure still refunded time across a
/// restart.
///
/// The previous pass added a backup copy and stopped there. A backup is older
/// than the primary by definition, so recovering from one hands back the
/// difference. OPSV put it precisely: primary 1200, backup 600, corrupt the
/// primary, and the child gets ten minutes.
///
/// THE INVARIANT THESE TESTS HOLD TO
/// ---------------------------------
/// A persistence failure may make KidShell stricter. It must never make
/// KidShell more permissive. Across any restart, used time must not decrease
/// because of a write failure, a crash, a corrupt file, a stale backup, a
/// deleted file, or an unavailable writer.
/// </summary>
public class ScreenTimeMonotonicTests
{
    private const string Today = "2026-09-29";

    private static ScreenTimeEngine Engine(
        IScreenTimeStateStore store, out FakeTimeProvider time, int minutes = 60)
    {
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = minutes;
        configuration.ScreenTime.WeekendMinutes = minutes;

        time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

        return new ScreenTimeEngine(
            new StubAppState(configuration), store, new RecordingLogger(), time);
    }

    private static void Spend(ScreenTimeEngine engine, FakeTimeProvider time, int seconds)
    {
        for (var i = 0; i < seconds / 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            engine.Tick();
        }
    }

    // ------------------------------------- the exact reported reproductions

    [Fact]
    public void Sixty_used_then_every_write_fails_then_restart_is_not_zero()
    {
        // OPSV: 60 used, all writes fail, restart => 0.
        //
        // The write-ahead marker is what closes this. If the session cannot be
        // recorded there is no durable statement that time is being used at
        // all, so enforcement is unavailable rather than silently permissive.
        var store = new InMemoryScreenTimeStateStore { FailWrites = true };
        var engine = Engine(store, out var time);

        Assert.True(engine.IsEnforcementUnavailable);
        Assert.True(engine.Evaluate().IsBlocked);

        Spend(engine, time, 60);

        var restarted = Engine(store, out _);

        Assert.True(restarted.Evaluate().IsBlocked, "a restart after total write failure granted a fresh day");
    }

    [Fact]
    public void Sixty_durable_then_writes_fail_then_restart_does_not_offer_the_rest_of_the_day()
    {
        // OPSV: 60 durable, 120 used, later writes fail, restart => 60.
        //
        // 60 is a floor, not a total: the child may have used any amount
        // after it. Carrying on from the floor credits them the difference.
        var store = new InMemoryScreenTimeStateStore();
        var engine = Engine(store, out var time);

        Spend(engine, time, 60);
        var durable = engine.State.UsedSeconds;
        Assert.True(durable > 0);

        store.FailWrites = true;
        Spend(engine, time, 60);

        var restarted = Engine(store, out _);

        Assert.True(restarted.State.UsedSeconds >= durable);
        Assert.True(restarted.Evaluate().IsBlocked, "an unclosed session was treated as a clean stop");
    }

    [Fact]
    public void A_corrupt_primary_never_falls_back_to_a_smaller_backup()
    {
        // OPSV: primary 1200, backup 600, corrupt primary => 600. Never.
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState
        {
            LocalDate = Today, UsedSeconds = 600, SessionState = ScreenTimeSessionState.Clean
        });

        store.Save(new ScreenTimeState
        {
            LocalDate = Today, UsedSeconds = 1200, SessionState = ScreenTimeSessionState.Clean
        });

        File.WriteAllText(path, "{ corrupt");

        var engine = Engine(store, out _);

        // The recorded figure is a floor that can be proven - 600, from the
        // backup - and the DECISION is what must not be permissive. The day
        // is blocked, so the ten minutes between the backup and the truth are
        // never handed out.
        //
        // Asserted as the decision rather than as the number, because
        // inflating the number to the allowance would be inventing a
        // measurement. What the product knows is "at least 600, and the
        // session did not close".
        var snapshot = engine.Evaluate();

        Assert.True(snapshot.IsBlocked, "a corrupt primary let the child carry on from the backup");
        Assert.True(engine.IsUsageUnknown);
        Assert.True(engine.State.UsedSeconds >= 600, "the proven floor was lowered");
    }

    [Fact]
    public void The_high_water_mark_takes_the_larger_of_two_copies()
    {
        var newer = new ScreenTimeState { LocalDate = Today, UsedSeconds = 1200 };
        var older = new ScreenTimeState { LocalDate = Today, UsedSeconds = 600 };

        Assert.Equal(1200, ScreenTimeJournalRules.HighWaterFor(Today, newer, older));
        Assert.Equal(1200, ScreenTimeJournalRules.HighWaterFor(Today, older, newer));
    }

    [Fact]
    public void Yesterdays_figure_is_not_todays_floor()
    {
        // A heavy Saturday must not block Sunday morning.
        var yesterday = new ScreenTimeState { LocalDate = "2026-09-28", UsedSeconds = 3600 };

        Assert.Equal(0, ScreenTimeJournalRules.HighWaterFor(Today, yesterday, null));
    }

    // ------------------------------------------------ the loss cases

    [Fact]
    public void A_deleted_primary_does_not_grant_a_fresh_day()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = Today, UsedSeconds = 600, SessionState = ScreenTimeSessionState.Clean });
        store.Save(new ScreenTimeState { LocalDate = Today, UsedSeconds = 1200, SessionState = ScreenTimeSessionState.Clean });

        File.Delete(path);

        Assert.True(Engine(store, out _).Evaluate().IsBlocked);
    }

    [Fact]
    public void Both_copies_gone_does_not_grant_a_fresh_day()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = Today, UsedSeconds = 600 });
        store.Save(new ScreenTimeState { LocalDate = Today, UsedSeconds = 1200 });

        File.WriteAllText(path, "{ corrupt");
        File.WriteAllText(store.BackupPath, "{ corrupt");

        Assert.True(Engine(store, out _).Evaluate().IsBlocked);
    }

    [Fact]
    public void An_unavailable_writer_blocks_rather_than_counting_into_memory()
    {
        var store = new InMemoryScreenTimeStateStore { FailWrites = true };
        var engine = Engine(store, out _);

        Assert.True(engine.IsEnforcementUnavailable);
        Assert.True(engine.Evaluate().IsBlocked);
    }

    [Fact]
    public void A_writer_that_fails_mid_session_stops_granting_time()
    {
        var store = new InMemoryScreenTimeStateStore();
        var engine = Engine(store, out var time);

        Assert.False(engine.Evaluate().IsBlocked);

        store.FailWrites = true;
        Spend(engine, time, 60);

        Assert.True(engine.IsEnforcementUnavailable);
        Assert.True(engine.Evaluate().IsBlocked);
    }

    // --------------------------------------------- crash versus shutdown

    [Fact]
    public void A_crash_before_the_checkpoint_fails_closed()
    {
        var store = new InMemoryScreenTimeStateStore();
        var engine = Engine(store, out var time);

        Spend(engine, time, 60);

        // No CloseSession: the process went away.
        var restarted = Engine(store, out _);

        Assert.True(restarted.Evaluate().IsBlocked);
    }

    [Fact]
    public void A_clean_shutdown_restarts_normally()
    {
        // The other half. A product that blocked the day after every ordinary
        // close would have replaced a refund with a lockout.
        var store = new InMemoryScreenTimeStateStore();
        var engine = Engine(store, out var time);

        Spend(engine, time, 60);
        var used = engine.State.UsedSeconds;

        engine.CloseSession();

        var restarted = Engine(store, out _);

        Assert.Equal(used, restarted.State.UsedSeconds);
        Assert.False(restarted.Evaluate().IsBlocked);
        Assert.False(restarted.IsUsageUnknown);
    }

    [Fact]
    public void A_parent_reset_recovers_a_failed_closed_day()
    {
        var store = new InMemoryScreenTimeStateStore();
        var engine = Engine(store, out var time);

        Spend(engine, time, 60);

        var restarted = Engine(store, out _);
        Assert.True(restarted.Evaluate().IsBlocked);

        restarted.ResetToday();

        Assert.False(restarted.IsUsageUnknown);
        Assert.False(restarted.Evaluate().IsBlocked);
    }

    [Fact]
    public void A_genuine_first_run_is_still_zero()
    {
        var engine = Engine(new InMemoryScreenTimeStateStore(), out _);

        Assert.Equal(0, engine.State.UsedSeconds);
        Assert.False(engine.Evaluate().IsBlocked);
        Assert.False(engine.IsUsageUnknown);
    }

    [Fact]
    public void The_failure_is_logged_once_rather_than_every_tick()
    {
        // A wedged disk must not fill the log with one line every thirty
        // seconds for the rest of the day.
        var logger = new RecordingLogger();
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = 60;

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryScreenTimeStateStore { FailWrites = true };
        var engine = new ScreenTimeEngine(new StubAppState(configuration), store, logger, time);

        for (var i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            engine.Tick();
        }

        var complaints = logger.Entries.Count(e =>
            e.Message.Contains("could not be persisted", StringComparison.OrdinalIgnoreCase));

        Assert.True(complaints <= 2, $"the persistence failure was logged {complaints} times");
    }

    [Fact]
    public void The_failed_closed_state_is_visible_rather_than_silent()
    {
        // A parent seeing "time is up" on a fresh morning deserves to know the
        // counter was damaged rather than consumed.
        var store = new InMemoryScreenTimeStateStore { FailWrites = true };
        var engine = Engine(store, out _);

        Assert.True(engine.IsEnforcementUnavailable);
    }

    private sealed class StubAppState : IAppStateService
    {
        public StubAppState(KidShellConfiguration configuration) => Current = configuration;

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
