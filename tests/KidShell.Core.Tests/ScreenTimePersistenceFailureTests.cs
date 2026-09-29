using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV FINDING 04 — a failed save refunded the time that had been used.
///
/// The reproduction OPSV reported: use 60 seconds, make the save fail, restart,
/// and the counter reads zero. A child who can cause a write to fail - by
/// filling the disk, or by deleting the file - gets an unlimited day.
///
/// The rule these tests hold to is one-directional. A failure may over-count,
/// and may cost the parent a reset. It may never hand back time that was
/// spent.
/// </summary>
public class ScreenTimePersistenceFailureTests
{
    private static ScreenTimeEngine Engine(
        IScreenTimeStateStore store, out FakeTimeProvider time, int minutes = 60)
    {
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = minutes;
        configuration.ScreenTime.WeekendMinutes = minutes;

        time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));

        return new ScreenTimeEngine(
            new StubState(configuration), store, new RecordingLogger(), time);
    }

    // ------------------------------------------------- the reported defect

    [Fact]
    public void A_save_failure_after_sixty_seconds_cannot_restart_at_zero()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var logger = new RecordingLogger();

        // A minute of real use, persisted properly.
        var store = new JsonScreenTimeStateStore(path, logger);
        var engine = Engine(store, out var time);

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(20));
            engine.Tick();
        }

        var used = engine.State.UsedSeconds;
        Assert.True(used >= 60, $"expected at least a minute of use, got {used}s");

        // Now the writes stop working, and more time passes.
        var blocked = new FailingStore(store);
        var second = Engine(blocked, out var laterTime);

        for (var i = 0; i < 3; i++)
        {
            laterTime.Advance(TimeSpan.FromSeconds(20));
            second.Tick();
        }

        Assert.True(second.IsPersistenceFailing);

        // Restart. The durable copy is the one from before the failures, which
        // is older and smaller - but it is not zero, and that is the rule.
        var afterRestart = Engine(new JsonScreenTimeStateStore(path, logger), out _);

        Assert.True(afterRestart.State.UsedSeconds >= used,
            $"a failed save refunded time: {afterRestart.State.UsedSeconds}s < {used}s");
    }

    [Fact]
    public void A_failed_replacement_preserves_the_previous_durable_state()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        Assert.True(store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 900 }));

        // The authoritative file must never be truncated before the new
        // content is safely on disk, so a failure leaves it exactly as it was.
        using (var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 1800 }));
        }

        Assert.Equal(900, store.Load().State.UsedSeconds);
    }

    // ------------------------------------------------------- primary/backup

    [Fact]
    public void A_corrupt_primary_falls_back_to_the_backup()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        Assert.True(store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 600 }));
        Assert.True(store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 1200 }));

        Assert.True(File.Exists(store.BackupPath), "a successful save must leave a backup behind");

        File.WriteAllText(path, "{ truncated");

        var load = store.Load();

        Assert.Equal(ScreenTimeLoadOutcome.RecoveredFromBackup, load.Outcome);
        Assert.Equal(600, load.State.UsedSeconds);
    }

    [Fact]
    public void A_deleted_primary_falls_back_to_the_backup_rather_than_to_zero()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 600 });
        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 1200 });

        // The obvious attack: delete the file that says how long you have been
        // on the computer.
        File.Delete(path);

        var load = store.Load();

        Assert.Equal(ScreenTimeLoadOutcome.RecoveredFromBackup, load.Outcome);
        Assert.True(load.State.UsedSeconds > 0);
    }

    [Fact]
    public void A_corrupt_backup_cannot_override_a_valid_primary()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 600 });
        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 1800 });

        File.WriteAllText(store.BackupPath, "{ rubbish");

        var load = store.Load();

        Assert.Equal(ScreenTimeLoadOutcome.Primary, load.Outcome);
        Assert.Equal(1800, load.State.UsedSeconds);
    }

    [Fact]
    public void Both_copies_unreadable_spends_the_day_rather_than_refunding_it()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 600 });
        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 1800 });

        File.WriteAllText(path, "{ rubbish");
        File.WriteAllText(store.BackupPath, "{ also rubbish");

        var load = store.Load();
        Assert.Equal(ScreenTimeLoadOutcome.Unreadable, load.Outcome);

        var engine = Engine(store, out _);

        Assert.True(engine.IsUsageUnknown);
        Assert.True(engine.Evaluate().IsBlocked, "an unknown counter must not read as a fresh day");
    }

    [Fact]
    public void A_parent_reset_clears_an_unreadable_counter()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var store = new JsonScreenTimeStateStore(path, new RecordingLogger());

        store.Save(new ScreenTimeState { LocalDate = "2026-09-23", UsedSeconds = 600 });
        File.WriteAllText(path, "{ rubbish");
        File.WriteAllText(store.BackupPath, "{ rubbish");

        var engine = Engine(store, out _);
        Assert.True(engine.Evaluate().IsBlocked);

        // The one authority that should be able to clear it: somebody who
        // knows the PIN. A child deleting a file is not that.
        engine.ResetToday();

        Assert.False(engine.IsUsageUnknown);
        Assert.False(engine.Evaluate().IsBlocked);
    }

    [Fact]
    public void A_genuine_first_run_really_is_zero()
    {
        using var dir = new TempDirectory();
        var store = new JsonScreenTimeStateStore(Path.Combine(dir.Path, "screentime.json"), new RecordingLogger());

        var load = store.Load();

        Assert.Equal(ScreenTimeLoadOutcome.FirstRun, load.Outcome);
        Assert.Equal(0, load.State.UsedSeconds);

        var engine = Engine(store, out _);
        Assert.False(engine.IsUsageUnknown);
        Assert.False(engine.Evaluate().IsBlocked);
    }

    // ------------------------------------------------- failure propagation

    [Fact]
    public void An_injected_io_failure_is_reported_rather_than_swallowed()
    {
        var store = new ThrowingStore();
        var engine = Engine(store, out var time);

        time.Advance(TimeSpan.FromSeconds(30));
        engine.Tick();

        Assert.True(engine.IsPersistenceFailing);
    }

    [Fact]
    public void Repeated_failures_keep_counting_in_memory()
    {
        // The current session still has to be limited. What a failure costs is
        // the record across a restart, not the limit itself.
        var store = new InMemoryScreenTimeStateStore { FailWrites = true };
        var engine = Engine(store, out var time, minutes: 5);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            engine.Tick();
        }

        Assert.True(engine.IsPersistenceFailing);
        Assert.True(engine.Evaluate().IsBlocked, "the in-memory counter must still enforce the limit");
    }

    [Fact]
    public void Recovering_from_a_failure_clears_the_flag()
    {
        var store = new InMemoryScreenTimeStateStore { FailWrites = true };
        var engine = Engine(store, out var time);

        time.Advance(TimeSpan.FromSeconds(30));
        engine.Tick();
        Assert.True(engine.IsPersistenceFailing);

        store.FailWrites = false;
        time.Advance(TimeSpan.FromSeconds(30));
        engine.Tick();

        Assert.False(engine.IsPersistenceFailing);
    }

    [Fact]
    public void A_successful_restart_keeps_the_usage_it_persisted()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "screentime.json");
        var logger = new RecordingLogger();

        var first = Engine(new JsonScreenTimeStateStore(path, logger), out var time);

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            first.Tick();
        }

        var used = first.State.UsedSeconds;
        Assert.True(used > 0);

        // An ordinary shutdown. Without it the next start sees an unclosed
        // session and fails closed, which is right for a crash and wrong for
        // a restart.
        first.CloseSession();

        var second = Engine(new JsonScreenTimeStateStore(path, logger), out _);

        Assert.Equal(used, second.State.UsedSeconds);
        Assert.False(second.IsUsageUnknown);
    }

    // ------------------------------------------------------------- doubles

    /// <summary>A store whose writes always fail, the way a full disk does.</summary>
    private sealed class FailingStore : IScreenTimeStateStore
    {
        private readonly IScreenTimeStateStore _inner;

        public FailingStore(IScreenTimeStateStore inner) => _inner = inner;

        public ScreenTimeStateLoad Load() => _inner.Load();

        public bool Save(ScreenTimeState state) => false;
    }

    /// <summary>A store that throws, the way a permission failure does.</summary>
    private sealed class ThrowingStore : IScreenTimeStateStore
    {
        public ScreenTimeStateLoad Load() => ScreenTimeStateLoad.FirstRun();

        public bool Save(ScreenTimeState state) => false;
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
