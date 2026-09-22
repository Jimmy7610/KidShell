using System.Text.Json.Serialization;

namespace KidShell.Core.ScreenTime;

/// <summary>
/// How much time has been used, persisted across restarts.
///
/// Kept separate from <see cref="Configuration.ScreenTimeSettings"/> on
/// purpose: settings are what a parent chose and change rarely, state is what
/// the child did and changes every minute. Mixing them would mean rewriting
/// the parent's configuration constantly, and a crash mid-write would lose
/// both.
/// </summary>
public sealed class ScreenTimeState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// The local date this state belongs to, as yyyy-MM-dd.
    ///
    /// A string rather than a DateTime because what matters is "which day was
    /// this", and a DateTime invites time-zone arithmetic that produces the
    /// wrong answer across a DST boundary.
    /// </summary>
    public string LocalDate { get; set; } = string.Empty;

    /// <summary>Seconds used today. Seconds, so a short session still counts.</summary>
    public int UsedSeconds { get; set; }

    /// <summary>Extra minutes a parent granted today. Reset with the day.</summary>
    public int BonusMinutes { get; set; }

    /// <summary>True when a parent lifted the limit for the rest of today.</summary>
    public bool UnlimitedForToday { get; set; }

    /// <summary>
    /// Last time the counter was updated, in UTC. Used to detect the clock
    /// moving backwards between sessions.
    /// </summary>
    public DateTimeOffset? LastUpdatedUtc { get; set; }

    /// <summary>
    /// How many times a backwards clock jump has been seen. Recorded and
    /// surfaced; KidShell does not fight it.
    /// </summary>
    public int SuspiciousClockEvents { get; set; }

    [JsonIgnore]
    public TimeSpan Used => TimeSpan.FromSeconds(Math.Max(0, UsedSeconds));

    public ScreenTimeState Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        LocalDate = LocalDate,
        UsedSeconds = UsedSeconds,
        BonusMinutes = BonusMinutes,
        UnlimitedForToday = UnlimitedForToday,
        LastUpdatedUtc = LastUpdatedUtc,
        SuspiciousClockEvents = SuspiciousClockEvents
    };
}

/// <summary>What the child and the UI are told right now.</summary>
public enum ScreenTimeStatus
{
    /// <summary>No limit configured. Time is not counted against anything.</summary>
    NotLimited = 0,

    /// <summary>Time remaining, nothing to say yet.</summary>
    Running = 1,

    /// <summary>Inside a warning threshold.</summary>
    Warning = 2,

    /// <summary>Today's allowance is gone.</summary>
    Expired = 3,

    /// <summary>Outside the hours the child is allowed to use the computer.</summary>
    OutsideAllowedHours = 4
}

/// <summary>A snapshot of the screen-time situation.</summary>
public sealed record ScreenTimeSnapshot
{
    public required ScreenTimeStatus Status { get; init; }

    public required TimeSpan Used { get; init; }

    public required TimeSpan Allowance { get; init; }

    /// <summary>Never negative: "minus five minutes" is not a useful thing to show a child.</summary>
    public TimeSpan Remaining => Allowance > Used ? Allowance - Used : TimeSpan.Zero;

    /// <summary>The warning threshold currently crossed, if any.</summary>
    public int? WarningMinutes { get; init; }

    public bool IsWeekend { get; init; }

    public bool HasBonus { get; init; }

    public bool ClockLooksTampered { get; init; }

    public bool IsBlocked => Status is ScreenTimeStatus.Expired or ScreenTimeStatus.OutsideAllowedHours;
}
