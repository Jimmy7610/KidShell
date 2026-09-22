namespace KidShell.Core.Configuration;

/// <summary>
/// Screen-time configuration: what a parent chose.
///
/// Separate from the running counter (KidShell.Core.ScreenTime.ScreenTimeState),
/// which changes every minute. Mixing them would mean rewriting the parent's
/// configuration constantly, and a crash mid-write would lose both.
/// </summary>
public sealed class ScreenTimeSettings
{
    /// <summary>Earliest hour the child may use the computer, 0-23.</summary>
    public const int DefaultStartHour = 7;

    /// <summary>Hour after which the computer is no longer allowed, 0-24.</summary>
    public const int DefaultEndHour = 20;

    public bool IsEnabled { get; set; }

    public int WeekdayMinutes { get; set; } = 60;

    public int WeekendMinutes { get; set; } = 120;

    /// <summary>
    /// Whether a daily time window applies on top of the allowance. A child
    /// with an hour left at 23:00 should still be going to bed.
    /// </summary>
    public bool RestrictHours { get; set; }

    public int AllowedFromHour { get; set; } = DefaultStartHour;

    public int AllowedUntilHour { get; set; } = DefaultEndHour;

    /// <summary>
    /// Minutes-remaining thresholds at which the child is warned. Descending;
    /// the engine picks the largest one that has been crossed.
    /// </summary>
    public List<int> WarningMinutes { get; set; } = [15, 5, 1];

    /// <summary>Allowance for a given day, in minutes.</summary>
    public int MinutesFor(DayOfWeek day) =>
        day is DayOfWeek.Saturday or DayOfWeek.Sunday ? WeekendMinutes : WeekdayMinutes;

    public static bool IsWeekend(DayOfWeek day) => day is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public ScreenTimeSettings Clone() => new()
    {
        IsEnabled = IsEnabled,
        WeekdayMinutes = WeekdayMinutes,
        WeekendMinutes = WeekendMinutes,
        RestrictHours = RestrictHours,
        AllowedFromHour = AllowedFromHour,
        AllowedUntilHour = AllowedUntilHour,
        WarningMinutes = [.. WarningMinutes]
    };
}
