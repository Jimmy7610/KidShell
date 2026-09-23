using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The piece that turns the screen-time engine from a tested calculator into a
/// feature.
///
/// The engine was correct and complete for a long time while nothing called it:
/// registered in dependency injection, consumed nowhere. These tests exist so
/// that cannot happen again quietly - they describe the behaviour a child and a
/// parent actually experience.
/// </summary>
public class ScreenTimeCoordinatorTests
{
    private static (ScreenTimeCoordinator Coordinator, FakeTimeProvider Time, ScreenTimeEngine Engine, StubAppState State)
        Build(int weekdayMinutes = 60, bool enabled = true, bool restrictHours = false,
              int fromHour = 7, int untilHour = 20)
    {
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = enabled;
        configuration.ScreenTime.WeekdayMinutes = weekdayMinutes;
        configuration.ScreenTime.WeekendMinutes = weekdayMinutes;
        configuration.ScreenTime.RestrictHours = restrictHours;
        configuration.ScreenTime.AllowedFromHour = fromHour;
        configuration.ScreenTime.AllowedUntilHour = untilHour;

        var state = new StubAppState(configuration);
        var logger = new RecordingLogger();

        // A Wednesday at 10:00, comfortably inside any allowed window.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));

        var engine = new ScreenTimeEngine(state, new InMemoryScreenTimeStore(), logger, time);

        return (new ScreenTimeCoordinator(engine, state, logger), time, engine, state);
    }

    [Fact]
    public void A_fresh_day_allows_launching()
    {
        var (coordinator, _, _, _) = Build();

        Assert.True(coordinator.CanLaunch());
        Assert.False(coordinator.Current.IsBlocked);
    }

    [Fact]
    public void Launching_is_refused_once_the_allowance_is_gone()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 30);

        // Consume the allowance in credited ticks, the way real use does.
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            engine.Tick();
        }

        Assert.False(coordinator.CanLaunch());
    }

    [Fact]
    public void Screen_time_switched_off_never_blocks()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 1, enabled: false);

        time.Advance(TimeSpan.FromHours(3));
        engine.Tick();

        // The counter may say anything; with the feature off it must not gate
        // a launch.
        Assert.True(coordinator.CanLaunch());
    }

    [Fact]
    public void Sleeping_does_not_consume_the_allowance()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 60);

        // A laptop closed for three hours. The engine caps each credited tick,
        // so resuming must not have eaten the day.
        time.Advance(TimeSpan.FromHours(3));
        engine.Tick();

        Assert.True(coordinator.CanLaunch());

        var remaining = coordinator.Current.Snapshot.Remaining;
        Assert.True(remaining > TimeSpan.FromMinutes(50),
            $"a long sleep should not consume the allowance, but {remaining.TotalMinutes:F0} minutes remained");
    }

    [Fact]
    public void A_parent_granting_time_unblocks_immediately()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 10);

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            engine.Tick();
        }

        Assert.False(coordinator.CanLaunch());

        // Granting has to take effect now, not after the parent saves.
        engine.GrantExtension(30);
        coordinator.Refresh();

        Assert.True(coordinator.CanLaunch());
    }

    [Fact]
    public void Rest_of_day_removes_the_limit_without_removing_the_setting()
    {
        var (coordinator, time, engine, state) = Build(weekdayMinutes: 5);

        // Several ticks, not one long jump: the engine caps how much a single
        // tick may credit, so one three-hour tick would credit five minutes.
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromMinutes(4));
            engine.Tick();
        }

        Assert.False(coordinator.CanLaunch());

        engine.GrantRestOfDay();
        coordinator.Refresh();

        Assert.True(coordinator.CanLaunch());

        // Tomorrow's allowance is untouched: this was a decision about today.
        Assert.Equal(5, state.Current.ScreenTime.WeekdayMinutes);
    }

    [Fact]
    public void Outside_the_allowed_hours_blocks_even_with_time_left()
    {
        var (coordinator, _, _, _) = Build(weekdayMinutes: 120, restrictHours: true, fromHour: 7, untilHour: 9);

        // 10:00, an hour after the window closed, with two hours unused. A
        // child with time left at bedtime should still be going to bed.
        Assert.False(coordinator.CanLaunch());
        Assert.Equal(ScreenTimeStatus.OutsideAllowedHours, coordinator.Current.Snapshot.Status);
    }

    [Fact]
    public void A_warning_is_raised_once_and_not_repeated()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 20);

        var warnings = new List<int>();
        coordinator.Changed += (_, view) =>
        {
            if (view.NewWarningMinutes is { } minutes)
            {
                warnings.Add(minutes);
            }
        };

        // Run down to inside the 15-minute threshold.
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            engine.Tick();
            coordinator.Refresh();
        }

        Assert.Contains(15, warnings);

        var countBefore = warnings.Count;
        coordinator.AcknowledgeWarning(15);

        // Acknowledged means acknowledged: the same threshold must not nag
        // every thirty seconds.
        coordinator.Refresh();
        coordinator.Refresh();

        Assert.Equal(countBefore, warnings.Count);
    }

    [Fact]
    public void A_later_warning_still_arrives_after_an_earlier_one_was_dismissed()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 20);

        var warnings = new List<int>();
        coordinator.Changed += (_, view) =>
        {
            if (view.NewWarningMinutes is { } minutes)
            {
                warnings.Add(minutes);
            }
        };

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            engine.Tick();
            coordinator.Refresh();
        }

        coordinator.AcknowledgeWarning(15);

        // Keep going until fewer than five minutes remain. Eight minutes were
        // used above, so ten more takes a twenty-minute allowance to two left.
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            engine.Tick();
            coordinator.Refresh();
        }

        Assert.Contains(5, warnings);
    }

    [Fact]
    public void Warnings_reset_at_the_day_boundary()
    {
        var (coordinator, time, engine, _) = Build(weekdayMinutes: 20);

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            engine.Tick();
            coordinator.Refresh();
        }

        coordinator.AcknowledgeWarning(15);

        // Next day. Crossing the same threshold must warn again - yesterday's
        // dismissal was about yesterday.
        time.Advance(TimeSpan.FromDays(1));
        engine.Tick();

        var warnings = new List<int>();
        coordinator.Changed += (_, view) =>
        {
            if (view.NewWarningMinutes is { } minutes)
            {
                warnings.Add(minutes);
            }
        };

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(2));
            engine.Tick();
            coordinator.Refresh();
        }

        Assert.Contains(15, warnings);
    }

    [Fact]
    public void Stopping_the_coordinator_is_safe_to_repeat()
    {
        var (coordinator, _, _, _) = Build();

        coordinator.Start();
        coordinator.Stop();
        coordinator.Stop();
        coordinator.Dispose();

        // Disposing then starting must not resurrect the timer.
        coordinator.Start();
        Assert.True(coordinator.CanLaunch());
    }

    /// <summary>A configuration holder backed by an object rather than a file.</summary>
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

    /// <summary>Keeps the screen-time counter in memory.</summary>
    private sealed class InMemoryScreenTimeStore : IScreenTimeStateStore
    {
        private ScreenTimeState _state = new();

        public ScreenTimeState Load() => _state.Clone();

        public bool Save(ScreenTimeState state)
        {
            _state = state.Clone();
            return true;
        }
    }
}
