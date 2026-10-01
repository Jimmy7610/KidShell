using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;

namespace KidShell.Core.ScreenTime;

/// <summary>Persists the running counter.</summary>
public interface IScreenTimeStateStore
{
    /// <summary>
    /// Reads the counter, saying where it came from.
    ///
    /// The outcome is part of the answer rather than a detail of the
    /// implementation: "there has never been a counter" and "there was one and
    /// it cannot be read" are different facts, and collapsing them into a
    /// fresh zero is how a failed write turned into free time.
    /// </summary>
    ScreenTimeStateLoad Load();

    /// <summary>
    /// Writes the counter. False means it did not reach the disk, and the
    /// caller is expected to care - this is security state, not telemetry.
    /// </summary>
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
    private readonly IScreenTimeParentAuthority? _parentAuthority;

    private ScreenTimeState _current;
    private long _lastTickStamp;

    public ScreenTimeEngine(
        IAppStateService state,
        IScreenTimeStateStore store,
        IKidShellLogger logger,
        TimeProvider? time = null,
        IScreenTimeParentAuthority? parentAuthority = null)
    {
        _state = state;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _parentAuthority = parentAuthority;

        var load = _store.Load();
        _current = load.State;
        _lastTickStamp = _time.GetTimestamp();

        // OPSV RETEST 2, FINDING 03. What a restart may conclude is decided by
        // ScreenTimeJournalRules, not by which copy happened to be readable.
        //
        // The old code took the newest READABLE counter, and a backup is older
        // than the primary by definition - so a corrupt primary holding 1200
        // was answered with a backup holding 600 and the child got ten minutes
        // back. Recovery may now raise the figure and never lower it, and
        // where the true figure cannot be established the day is spent.
        var recovery = ScreenTimeJournalRules.Recover(
            Today, load.Outcome, load.Primary ?? load.State, load.Backup);

        if (recovery.UsedSeconds > _current.UsedSeconds)
        {
            _current.LocalDate = Today;
            _current.UsedSeconds = recovery.UsedSeconds;
        }

        _isUsageUnknown = recovery.MustFailClosed;

        if (_isUsageUnknown)
        {
            _logger.Warning("ScreenTime",
                $"Screen-time usage cannot be proven ({recovery.Reason}); " +
                "today is treated as spent until a parent resets it.");
        }

        RollOverIfNewDay();

        // Write-ahead. The session is recorded as OPEN before a single second
        // is credited, so a crash or a failed write cannot look like a clean
        // stop. If it cannot be written there is no durable record that time
        // is being used at all, and enforcement says so rather than counting
        // into memory nobody will read back.
        OpenSession();
    }

    /// <summary>Raised when the status changes, so the UI can react once.</summary>
    public event EventHandler<ScreenTimeSnapshot>? StatusChanged;

    /// <summary>
    /// Whether the counter could not be read at startup.
    ///
    /// The day is treated as spent while this is true. Surfaced rather than
    /// hidden: a parent seeing "time is up" on a fresh morning deserves to
    /// know the counter was damaged rather than consumed.
    /// </summary>
    public bool IsUsageUnknown => _isUsageUnknown;

    /// <summary>
    /// Whether the most recent write failed to reach the disk.
    ///
    /// The in-memory counter keeps advancing while this is true, so the
    /// current session is still limited; what is lost is the record across a
    /// restart, and the backup copy is what covers that.
    /// </summary>
    public bool IsPersistenceFailing => _persistenceFailing;

    public ScreenTimeState State => _current;

    private bool _isUsageUnknown;
    private bool _persistenceFailing;

    /// <summary>
    /// Whether enforcement has stopped being durable.
    ///
    /// True when the session could not be recorded as open, or when writes
    /// have failed since. It is fail-closed rather than best-effort: the
    /// alternative is counting in memory that no restart will ever read back,
    /// which is exactly how "all writes fail, restart, zero" happened.
    /// </summary>
    public bool IsEnforcementUnavailable => _enforcementUnavailable;

    private bool _enforcementUnavailable;

    /// <summary>
    /// Records that a session is running, before any time is credited.
    ///
    /// The write-ahead half of the invariant. A durable Open marker is what
    /// lets a later restart tell "the child stopped at 60" from "the last
    /// thing we managed to write was 60".
    /// </summary>
    private void OpenSession()
    {
        _current.SessionState = ScreenTimeSessionState.Open;
        _current.Sequence++;

        if (Persist())
        {
            _enforcementUnavailable = false;
            return;
        }

        // Nothing durable says time is being used. Continuing would grant the
        // child an unbounded session that no restart can account for.
        _enforcementUnavailable = true;

        _logger.Error("ScreenTime",
            "The screen-time session could not be recorded. Enforcement is unavailable and " +
            "further use is refused until it can be written.");
    }

    /// <summary>
    /// Records that the session ended tidily.
    ///
    /// Called on shutdown. Without it every restart looks like a crash, which
    /// would be correct-but-useless: a product that blocks the day after every
    /// ordinary close has replaced a refund with a lockout.
    /// </summary>
    public void CloseSession()
    {
        if (_enforcementUnavailable)
        {
            return;
        }

        _current.SessionState = ScreenTimeSessionState.Clean;
        _current.Sequence++;

        Persist();
    }

    private ScreenTimeSettings Settings => _state.Current.ScreenTime;

    /// <summary>
    /// Writes the counter and notices when it did not work.
    ///
    /// Every call site used to discard this. A failed save is not a logging
    /// concern: it is the difference between a limit that survives a restart
    /// and one that does not.
    /// </summary>
    private bool Persist()
    {
        var ok = _store.Save(_current);

        if (ok)
        {
            if (_persistenceFailing)
            {
                _logger.Info("ScreenTime", "The screen-time counter is being persisted again.");
            }

            _persistenceFailing = false;
            return true;
        }

        if (!_persistenceFailing)
        {
            _logger.Error("ScreenTime",
                "The screen-time counter could not be persisted. Counting continues in memory; " +
                "a restart will fall back to the last durable value, which never refunds time.");
        }

        _persistenceFailing = true;

        // A failed checkpoint means the durable figure is now behind the real
        // one by an unknown amount. Carrying on in memory is what made a
        // restart look like a refund, so enforcement stops being available
        // instead.
        _enforcementUnavailable = true;

        return false;
    }

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

        // Detection first, rollover second. It used to be the other way round,
        // and the order was the bug: rolling over writes a fresh
        // LastUpdatedUtc, and that field is the only thing the tamper check
        // has to compare against. A clock wound back over midnight therefore
        // reset the counter and then erased the evidence that it had.
        DetectClockTampering();
        RollOverIfNewDay();

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
            Persist();
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

        // The counter existed and could not be read, or the durable record
        // has stopped advancing. How much of today has gone is unknown, and
        // the only answer that cannot hand out free time is "all of it". A
        // parent clears this with the reset they already have; a child cannot
        // clear it by deleting a file, which is the attack this closes.
        if (_isUsageUnknown || _enforcementUnavailable)
        {
            return Snapshot(ScreenTimeStatus.Expired, allowance, allowance, null, isWeekend);
        }

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

        if (TryParentAuthority(a => a.GrantMinutes(minutes), $"{minutes} extra minutes"))
        {
            var granted = Evaluate();
            StatusChanged?.Invoke(this, granted);
            return granted;
        }

        _current.BonusMinutes += minutes;
        Persist();

        _logger.Info("ScreenTime", $"A parent granted {minutes} extra minutes today.");

        var snapshot = Evaluate();
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>Lifts the limit for the rest of today. Reset at the day boundary.</summary>
    public ScreenTimeSnapshot GrantRestOfDay()
    {
        RollOverIfNewDay();

        if (TryParentAuthority(a => a.GrantRestOfDay(), "an unlimited day"))
        {
            var granted = Evaluate();
            StatusChanged?.Invoke(this, granted);
            return granted;
        }

        _current.UnlimitedForToday = true;
        Persist();

        _logger.Info("ScreenTime", "A parent lifted today's screen-time limit.");

        var snapshot = Evaluate();
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>Clears today's usage. A deliberate parent action.</summary>
    public ScreenTimeSnapshot ResetToday()
    {
        if (TryParentAuthority(a => a.ResetToday(), "a reset"))
        {
            // The privileged side cleared the counter and the local state has
            // been reloaded from it. The two flags go with it: a parent
            // saying "start today again" is the deliberate act by somebody
            // with authority that these were waiting for.
            _isUsageUnknown = false;
            _enforcementUnavailable = false;

            var reset = Evaluate();
            StatusChanged?.Invoke(this, reset);
            return reset;
        }

        _current = new ScreenTimeState
        {
            LocalDate = Today,
            LastUpdatedUtc = _time.GetUtcNow(),

            // Kept: the count is evidence about the machine, not about today.
            SuspiciousClockEvents = _current.SuspiciousClockEvents
        };

        // A parent saying "start today again" is the one thing that should
        // clear an unreadable counter or a broken session. It is a deliberate
        // act by somebody who knows the PIN, which is exactly the authority
        // this state was protecting.
        _isUsageUnknown = false;
        _enforcementUnavailable = false;
        _current.SessionState = ScreenTimeSessionState.Open;

        Persist();
        _logger.Info("ScreenTime", "Today's screen-time counter was reset by a parent.");

        var snapshot = Evaluate();
        StatusChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>
    /// Routes a parent's grant to the privileged side, when there is one.
    ///
    /// PRIVILEGED BROKER HARDENING. These three changes make the child's
    /// situation LOOSER, which is the one direction the transition rules
    /// refuse from the child's own session - and rightly, because a state
    /// the child's process composes is a state the child's process chose.
    /// So the engine does not compose one: it asks for the change, and the
    /// privileged side applies it to the counter it holds.
    ///
    /// The local state is then reloaded rather than guessed at. Keeping a
    /// second copy of what the grant must have produced is how two sides of
    /// a boundary start disagreeing.
    ///
    /// Returns false when there is no privileged side - a development build,
    /// or a test - and the caller falls back to changing the state here.
    /// </summary>
    private bool TryParentAuthority(Func<IScreenTimeParentAuthority, bool> act, string what)
    {
        if (_parentAuthority is not { IsAvailable: true })
        {
            return false;
        }

        if (!act(_parentAuthority))
        {
            _logger.Error("ScreenTime",
                $"The security service did not apply {what}. Nothing was changed.");

            return false;
        }

        var load = _store.Load();
        _current = load.Primary ?? load.State;

        _logger.Info("ScreenTime", $"The security service applied {what}.");
        return true;
    }

    /// <summary>
    /// Starts a new day when the local date has moved FORWARD.
    ///
    /// Compares date strings rather than doing 24-hour arithmetic: on the
    /// night the clocks change a day is 23 or 25 hours, and anything computing
    /// "midnight plus 24 hours" is wrong twice a year. The keys are
    /// yyyy-MM-dd, so an ordinal comparison orders them exactly as dates.
    ///
    /// The direction is the point. This used to ask only whether the date had
    /// CHANGED, and a clock wound back over midnight changes it - so winding
    /// the clock back into yesterday read as an ordinary new morning and
    /// handed back the whole allowance. Which is, of course, precisely what
    /// somebody moving the clock was hoping for.
    ///
    /// A backward date therefore keeps the counter and is recorded instead.
    /// The trade is deliberate: a parent correcting a clock that was wrongly
    /// set into the future will find today's usage already spent, and can give
    /// time back with the extension button. A child who discovers that moving
    /// the clock earns another hour has found a hole in the product.
    /// </summary>
    private void RollOverIfNewDay()
    {
        var today = Today;

        if (string.Equals(_current.LocalDate, today, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_current.LocalDate) &&
            string.CompareOrdinal(today, _current.LocalDate) < 0)
        {
            _logger.Warning("ScreenTime",
                $"The local date moved backwards from {_current.LocalDate} to {today}; " +
                "today's usage is kept and the clock change is recorded.");

            RecordSuspiciousClock();
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

        Persist();
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
        //
        // UTC, not local time, which is what makes daylight saving a non-event
        // here: the clocks going back in the autumn move local time, never
        // UTC, so an honest October morning is not mistaken for tampering.
        if (now < last - TimeSpan.FromMinutes(1))
        {
            _logger.Warning("ScreenTime",
                $"The clock moved backwards by {(last - now).TotalMinutes:F0} minutes since the last session.");
            RecordSuspiciousClock();
        }
    }

    /// <summary>
    /// Notes a clock change and keeps the record.
    ///
    /// Deliberately does not touch LastUpdatedUtc: that field is the evidence
    /// the next check compares against, and overwriting it here would hide the
    /// second half of a clock change the same way the old rollover hid the
    /// first.
    /// </summary>
    private void RecordSuspiciousClock()
    {
        _current.SuspiciousClockEvents++;
        Persist();
    }
}
