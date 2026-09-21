namespace KidShell.Core.Configuration;

/// <summary>
/// Screen-time configuration. MVP 0.1 persists these values but does NOT
/// enforce them - enforcement arrives with the session watchdog milestone.
/// </summary>
public sealed class ScreenTimeSettings
{
    public bool IsEnabled { get; set; }

    public int WeekdayMinutes { get; set; } = 60;

    public int WeekendMinutes { get; set; } = 120;

    public ScreenTimeSettings Clone() => new()
    {
        IsEnabled = IsEnabled,
        WeekdayMinutes = WeekdayMinutes,
        WeekendMinutes = WeekendMinutes
    };
}
