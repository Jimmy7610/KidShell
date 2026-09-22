using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.ScreenTime;

/// <summary>Persists the running counter.</summary>
public interface IScreenTimeStateStore
{
    ScreenTimeState Load();

    bool Save(ScreenTimeState state);
}

/// <summary>
/// Tracks and enforces screen time at the application level.
///
/// WHAT THIS CAN AND CANNOT DO
/// ---------------------------
/// It can stop KidShell from launching apps, and it can put the child's screen
/// into a locked state. It cannot stop a child leaving KidShell and using the
/// rest of Windows, because no Windows restriction has been applied. The UI
/// says so; pretending otherwise would be the most damaging kind of overclaim,
/// since a parent who believes the computer switches itself off supervises
/// less.
///
/// DESIGN NOTES
/// ------------
/// * Time is accumulated in **seconds**, from a monotonic tick rather than by
///   comparing wall-clock stamps, so changing the system clock mid-session
///   does not hand the child extra time.
/// * The day boundary is a **local date string**, not a 24-hour arithmetic
///   window. On the night the clocks change, a day is 23 or 25 hours long, and
///   anything computing "midnight plus 24 hours" is wrong twice a year.
/// * State is saved on every tick that changes it, so a crash or a power cut
///   costs at most one tick rather than the whole session.
/// </summary>
public sealed class ScreenTimeEngine
{
    /// <summary>
    /// A tick longer than this is treated as the machine having been asleep,
    /// and is not counted. Otherwise closing the lid overnight would consume
    /// the whole allowance.
    /// </summary>
    public static readonly TimeSpan MaximumCreditedTick = TimeSpan.FromMinutes(5);

    private readonly IAppStateService _state;
    private readonly IScreenTimeStateStore _store;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;

    private ScreenTimeState _current;
    private long _lastTickStamp;

    public ScreenTimeEngine(
        IAppStateService state,
        IScreenTimeStateStore store,
        IKidShellLogger logger,
        TimeProvider? time = null)
    {
        _state = state;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        _current = _store.Load();
        _lastTickStamp = _time.GetTimestamp();

        RollOverIfNewDay();
    }

    /// <summary>Raised when the status changes, so the UI can react once.</summary>
    public event EventHandler<ScreenTimeSnapshot>? StatusChanged;

    public ScreenTimeState State => _current;

    private ScreenTimeSettings Settings => _state.Current.ScreenTime;

    /// <summary>The local date, as the key a day's usage is stored under.</summary>
    public string Today => LocalDateKey(_time.GetLocalNow());

    public static string LocalDateKey(DateTimeOffset localNow) => localNow.ToString("yyyy-MM-dd");

    /// <summary>
    /// Credits elapsed time and returns the situation.
    ///
    /// Called on a timer while a child session is active. Not called when the
    /// child is not using the machine, which is what makes "used time" mean
    /// time actually spent.
    /// </summary>
    public ScreenTimeSnapshot Tick()
    {
        var previous = Evaluate();

        RollOverIfNewDay();
        DetectClockTampering();

        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastTickStamp, now);
        _lastTickStamp = now;

        // A very long gap means sleep, hibernate or a stopped process. The
        // child was not using the computer, so it does not count.
        if (elapsed > MaximumCreditedTick)
        {
            _logger.Info("ScreenTime",
                $"Ignoring a {elapsed.TotalMinutes:F0} minute gap; the machine was probably asleep.");
            elapsed = TimeSpan.Zero;
        }

        if (elapsed > TimeSpan.Zero && Settings.IsEnabled)
        {
            _current.UsedSeconds += (int)elapsed.TotalSeconds;
            _current.LastUpdatedUtc = _time.GetUtcNow();

            // Saved every tick: a crash should cost one tick, not a session.
            _store.Save(_current);
        }

        var snapshot = Evaluate();

        if (snapshot.Status != previous.Status || snapshot.WarningMinutes != previous.WarningMinutes)
        {
            StatusChanged?.Invoke(this, snapshot);
        }

        return snapshot;
    }

    /// <summary>The current situation, without crediting any time.</summary>
    public ScreenTimeSnapshot Evaluate()
    {
        var settings = Settings;
        var localNow = _time.GetLocalNow();
        var isWeekend = ScreenTimeSettings.IsWeekend(localNow.DayOfWeek);

        if (!settings.IsEnabled)
        {
            return new ScreenTimeSnapshot
            {
                Status = ScreenTimeStatus.NotLimited,
                Used = _current.Used,
                Allowance = TimeSpan.Zero,
                IsWeekend = isWeekend,
                ClockLooksTampered = _current.SuspiciousClockEvents > 0
            };
        }

        if (_current.UnlimitedForToday)
        {
            return new ScreenTimeSnapshot
            {
                Status = ScreenTimeStatus.NotLimited,
                Used = _current.Used,
                Allowance = TimeSpan.MaxValue,
                IsWeekend = isWeekend,
                HasBonus = true,
                ClockLooksTampered = _current.SuspiciousClockEvents > 0
            };
        }

        var allowance = TimeSpan.FromMinutes(
            settings.MinutesFor(localNow.DayOfWeek) + Math.Max(0, _current.BonusMinutes));

        var used = _current.Used;
        var remaining = allowance > used ? allowance - used : TimeSpan.Zero;

        // Hours are checked before the allowance: a child with an hour left at
        // 23:00 should still be going to bed.
        if (settings.RestrictHours && !IsWithinAllowedHours(localNow, settings))
        {
            return Snapshot(ScreenTimeStatus.OutsideAllowedHours, used, allowance, null, isWeekend);
        }

        if (remaining <= TimeSpan.Zero)
        {
            return Snapshot(ScreenTimeStatus.Expired, used, allowance, null, isWeekend);
        }

        // Ascending, so the TIGHTEST applicable threshold wins. Ordering the
        // other way reports "15 minutes left" when four remain, which is both
        // wrong and the opposite of urgent.
        var warning = settings.WarningMinutes
            .Where(m => m > 0)
            .OrderBy(m => m)
            .Cast<int?>()
            .FirstOrDefault(m => remaining <= TimeSpan.FromMinutes(m!.Value));

        return Snapshot(
            warning is null ? ScreenTimeStatus.Running : ScreenTimeStatus.Warning,
            used, allowance, warning, isWeekend);
    }

    private ScreenTimeSnapshot Snapshot(
        ScreenTimeStatus status,
        TimeSpan used,
        TimeSpan allowance,
        int? warning,
        bool isWeekend) => new()
    {
        Status = status,
        Used = used,
        Allowance = allowance,
        WarningMinutes = warning,
        IsWeekend = isWeekend,
        HasBonus = _current.BonusMinutes > 0 || _current.UnlimitedForToday,
        ClockLooksTampered = _current.SuspiciousClockEvents > 0
    };

    /// <summary>
    /// Whether the current local time is inside the allowed window.
    ///
    /// A window that wraps past midnight (21:00 to 07:00) is handled, because
    /// a parent may well express "not during the night" that way.
    /// </summary>
    public static bool IsWithinAllowedHours(DateTimeOffset localNow, ScreenTimeSettings settings)
    {
        var from = Math.Clamp(settings.AllowedFromHour, 0, 23);
        var until = Math.Clamp(settings.AllowedUntilHour, 0, 24);
        var hour = localNow.Hour;

        if (from == until)
        {
            // A zero-width window would block everything, which is never what
            // a parent meant by setting the two the same.
            return true;
        }

        return from < until
            ? hour >= from && hour < until
            : hour >= from || hour < until;      // wraps past midnight
    }

    /// <summary>Grants extra minutes for today.</summary>
    public ScreenTimeSnapshot GrantExtension(int minutes)
    {
        if (minutes <= 0)
        {
            return Evaluate();
        }

        RollOverIfNewDay();

        _current.BonusMinutes += minutes;
        _store.Save(_current);

        _logger.Info("ScreenTime", $"A parent granted {minutes} extra minutes today.");

        var snapshot = Evaluate();
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>Lifts the limit for the rest of today. Reset at the day boundary.</summary>
    public ScreenTimeSnapshot GrantRestOfDay()
    {
        RollOverIfNewDay();

        _current.UnlimitedForToday = true;
        _store.Save(_current);

        _logger.Info("ScreenTime", "A parent lifted today's screen-time limit.");

        var snapshot = Evaluate();
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>Clears today's usage. A deliberate parent action.</summary>
    public ScreenTimeSnapshot ResetToday()
    {
        _current = new ScreenTimeState
        {
            LocalDate = Today,
            LastUpdatedUtc = _time.GetUtcNow(),

            // Kept: the count is evidence about the machine, not about today.
            SuspiciousClockEvents = _current.SuspiciousClockEvents
        };

        _store.Save(_current);
        _logger.Info("ScreenTime", "Today's screen-time counter was reset by a parent.");

        var snapshot = Evaluate();
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Starts a new day when the local date has changed.
    ///
    /// Compares date strings rather than doing 24-hour arithmetic: on the
    /// night the clocks change a day is 23 or 25 hours, and anything computing
    /// "midnight plus 24 hours" is wrong twice a year.
    /// </summary>
    private void RollOverIfNewDay()
    {
        var today = Today;

        if (string.Equals(_current.LocalDate, today, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_current.LocalDate))
        {
            _logger.Info("ScreenTime", $"New day ({today}); the screen-time counter starts again.");
        }

        _current = new ScreenTimeState
        {
            LocalDate = today,
            LastUpdatedUtc = _time.GetUtcNow(),
            SuspiciousClockEvents = _current.SuspiciousClockEvents
        };

        _store.Save(_current);
    }

    /// <summary>
    /// Notices the clock moving backwards between sessions.
    ///
    /// Deliberately passive: it is recorded, logged and shown to a parent, and
    /// nothing else. Fighting a clock change would mean tamper-proofing the
    /// machine, which is an arms race KidShell does not enter and could not
    /// win - and a parent who moves the clock legitimately should not be
    /// treated as an attacker.
    /// </summary>
    private void DetectClockTampering()
    {
        if (_current.LastUpdatedUtc is not { } last)
        {
            return;
        }

        var now = _time.GetUtcNow();

        // A minute of tolerance absorbs NTP corrections, which are normal.
        if (now < last - TimeSpan.FromMinutes(1))
        {
            _current.SuspiciousClockEvents++;
            _logger.Warning("ScreenTime",
                $"The clock moved backwards by {(last - now).TotalMinutes:F0} minutes since the last session.");
            _store.Save(_current);
        }
    }
}
