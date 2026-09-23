using KidShell.App.Localization;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using KidShell.Core.ScreenTime;

namespace KidShell.App.ViewModels.Parent;

/// <summary>
/// Föräldraläge → Skärmtid.
///
/// TWO KINDS OF STATE, KEPT APART
/// ------------------------------
/// The allowances and the daily window are *settings*: they live in the draft
/// configuration and take effect when the parent saves. The usage counter and
/// any bonus granted today are *running state*: they live in the screen-time
/// engine and take effect immediately.
///
/// Mixing them would be a bug a parent would feel. Granting "+15 minuter" has
/// to work right now, on the child's session, without waiting for a save - and
/// dragging the weekday slider must not silently change today's remaining time
/// before the parent has decided to keep it.
///
/// WHAT THIS IS HONEST ABOUT
/// -------------------------
/// Screen time is enforced at the application level: it stops KidShell
/// launching things and shows the child a time-is-up screen. Until a verified
/// secure configuration is active, the child can still leave KidShell, and the
/// page says so rather than implying the computer is locked.
/// </summary>
public sealed class ParentScreenTimeViewModel : ObservableObject
{
    /// <summary>The extensions a parent can grant in one tap.</summary>
    public static readonly int[] ExtensionOffers = [15, 30, 60];

    private readonly Action _onChanged;
    private readonly ScreenTimeEngine _engine;
    private readonly IAppStateService _state;

    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();
    private ScreenTimeSnapshot? _snapshot;
    private string? _grantMessage;

    public ParentScreenTimeViewModel(Action onChanged, ScreenTimeEngine engine, IAppStateService state)
    {
        _onChanged = onChanged;
        _engine = engine;
        _state = state;

        GrantExtensionCommand = new RelayCommand(parameter =>
        {
            if (TryReadMinutes(parameter, out var minutes))
            {
                GrantExtension(minutes);
            }
        });

        GrantRestOfDayCommand = new RelayCommand(GrantRestOfDay);
        ResetTodayCommand = new RelayCommand(ResetToday);

        Refresh();
    }

    public RelayCommand GrantExtensionCommand { get; }

    public RelayCommand GrantRestOfDayCommand { get; }

    public RelayCommand ResetTodayCommand { get; }

    public string Subtitle => Strings.Format("ScreenTime.Subtitle", _draft.Child.Name);

    // ------------------------------------------------------------ settings

    public bool IsEnabled
    {
        get => _draft.ScreenTime.IsEnabled;
        set
        {
            if (_draft.ScreenTime.IsEnabled == value)
            {
                return;
            }

            _draft.ScreenTime.IsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusSummary));
            OnPropertyChanged(nameof(HasUnsavedScreenTimeChange));
            _onChanged();
        }
    }

    public double WeekdayMinutes
    {
        get => _draft.ScreenTime.WeekdayMinutes;
        set
        {
            var minutes = (int)Math.Round(value);
            if (_draft.ScreenTime.WeekdayMinutes == minutes)
            {
                return;
            }

            _draft.ScreenTime.WeekdayMinutes = minutes;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WeekdayText));
            _onChanged();
        }
    }

    public double WeekendMinutes
    {
        get => _draft.ScreenTime.WeekendMinutes;
        set
        {
            var minutes = (int)Math.Round(value);
            if (_draft.ScreenTime.WeekendMinutes == minutes)
            {
                return;
            }

            _draft.ScreenTime.WeekendMinutes = minutes;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WeekendText));
            _onChanged();
        }
    }

    public string WeekdayText => FormatDuration(_draft.ScreenTime.WeekdayMinutes);

    public string WeekendText => FormatDuration(_draft.ScreenTime.WeekendMinutes);

    // -------------------------------------------------------- daily window

    /// <summary>
    /// Whether a daily window applies on top of the allowance. A child with an
    /// hour left at 23:00 should still be going to bed.
    /// </summary>
    public bool RestrictHours
    {
        get => _draft.ScreenTime.RestrictHours;
        set
        {
            if (_draft.ScreenTime.RestrictHours == value)
            {
                return;
            }

            _draft.ScreenTime.RestrictHours = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoursSummary));
            _onChanged();
        }
    }

    public double AllowedFromHour
    {
        get => _draft.ScreenTime.AllowedFromHour;
        set => SetHour(value, isStart: true);
    }

    public double AllowedUntilHour
    {
        get => _draft.ScreenTime.AllowedUntilHour;
        set => SetHour(value, isStart: false);
    }

    private void SetHour(double value, bool isStart)
    {
        var hour = Math.Clamp((int)Math.Round(value), 0, 24);
        var settings = _draft.ScreenTime;

        if (isStart)
        {
            if (settings.AllowedFromHour == hour)
            {
                return;
            }

            settings.AllowedFromHour = hour;

            // A window that ends before it starts would block the whole day
            // while looking like a setting. Push the other end rather than
            // silently producing nothing.
            if (settings.AllowedUntilHour <= hour)
            {
                settings.AllowedUntilHour = Math.Min(24, hour + 1);
                OnPropertyChanged(nameof(AllowedUntilHour));
            }
        }
        else
        {
            if (settings.AllowedUntilHour == hour)
            {
                return;
            }

            settings.AllowedUntilHour = hour;

            if (settings.AllowedFromHour >= hour)
            {
                settings.AllowedFromHour = Math.Max(0, hour - 1);
                OnPropertyChanged(nameof(AllowedFromHour));
            }
        }

        OnPropertyChanged(isStart ? nameof(AllowedFromHour) : nameof(AllowedUntilHour));
        OnPropertyChanged(nameof(HoursSummary));
        _onChanged();
    }

    public string HoursSummary => RestrictHours
        ? Strings.Format("ScreenTime.HoursSummary",
            $"{_draft.ScreenTime.AllowedFromHour:00}:00",
            $"{_draft.ScreenTime.AllowedUntilHour:00}:00")
        : Strings.Get("ScreenTime.HoursOff");

    // ------------------------------------------------------- today's state

    /// <summary>How much of today's allowance is gone, as a percentage.</summary>
    public double UsedPercentage
    {
        get
        {
            if (_snapshot is null || _snapshot.Allowance <= TimeSpan.Zero)
            {
                return 0;
            }

            return Math.Clamp(_snapshot.Used.TotalMinutes / _snapshot.Allowance.TotalMinutes * 100, 0, 100);
        }
    }

    public string UsedText => _snapshot is null
        ? FormatDuration(0)
        : FormatDuration((int)Math.Round(_snapshot.Used.TotalMinutes));

    public string RemainingText => _snapshot is null
        ? FormatDuration(0)
        : FormatDuration((int)Math.Round(_snapshot.Remaining.TotalMinutes));

    /// <summary>
    /// Whether the counter is actually running.
    ///
    /// This reads the SAVED configuration, not the draft, and the distinction
    /// matters: the engine counts against what was saved. A parent who has just
    /// switched the toggle on but not pressed Spara has changed nothing yet, and
    /// showing them a live figure derived from a setting that is not in force
    /// produces exactly the nonsense this used to print - "0 minuter kvar"
    /// under a toggle that looks switched on.
    /// </summary>
    public bool IsTrackingLive => _state.Current.ScreenTime.IsEnabled;

    /// <summary>
    /// Whether the draft's screen-time settings differ from what is saved, so
    /// the page can say that today's figures do not reflect them yet.
    /// </summary>
    public bool HasUnsavedScreenTimeChange
    {
        get
        {
            var saved = _state.Current.ScreenTime;
            var draft = _draft.ScreenTime;

            return saved.IsEnabled != draft.IsEnabled
                   || saved.WeekdayMinutes != draft.WeekdayMinutes
                   || saved.WeekendMinutes != draft.WeekendMinutes
                   || saved.RestrictHours != draft.RestrictHours
                   || saved.AllowedFromHour != draft.AllowedFromHour
                   || saved.AllowedUntilHour != draft.AllowedUntilHour;
        }
    }

    public string UnsavedNotice => Strings.Get("ScreenTime.UnsavedNotice");

    /// <summary>One line describing where today stands.</summary>
    public string StatusSummary
    {
        get
        {
            if (!IsTrackingLive)
            {
                return Strings.Get("ScreenTime.StatusOff");
            }

            if (_snapshot is null)
            {
                return Strings.Get("ScreenTime.StatusUnknown");
            }

            return _snapshot.Status switch
            {
                ScreenTimeStatus.Expired => Strings.Get("ScreenTime.StatusExpired"),
                ScreenTimeStatus.OutsideAllowedHours => Strings.Get("ScreenTime.StatusOutsideHours"),
                _ => Strings.Format("ScreenTime.StatusRemaining", RemainingText)
            };
        }
    }

    /// <summary>True when a bonus has been granted today, so the UI can say so.</summary>
    public bool HasBonusToday => _snapshot?.HasBonus ?? false;

    public string BonusText => Strings.Format("ScreenTime.BonusGranted",
        FormatDuration(_engine.State.BonusMinutes));

    /// <summary>
    /// Whether the clock has moved backwards in a way worth mentioning.
    ///
    /// Reported, never acted on. Usage comes from a monotonic clock, so winding
    /// the clock back does not refund time; turning this into an anti-tamper
    /// fight would be a losing arms race against a child with a settings app.
    /// </summary>
    public bool ClockLooksTampered => _snapshot?.ClockLooksTampered ?? false;

    /// <summary>Feedback after granting or resetting, cleared on the next load.</summary>
    public string? GrantMessage
    {
        get => _grantMessage;
        private set
        {
            _grantMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGrantMessage));
        }
    }

    public bool HasGrantMessage => !string.IsNullOrEmpty(_grantMessage);

    // ---------------------------------------------------------- extensions

    private void GrantExtension(int minutes)
    {
        // Applied to the running engine immediately: a parent granting more
        // time expects the child's screen to change now, not after a save.
        _snapshot = _engine.GrantExtension(minutes);
        GrantMessage = Strings.Format("ScreenTime.GrantedExtension", FormatDuration(minutes));
        Refresh();
    }

    private void GrantRestOfDay()
    {
        _snapshot = _engine.GrantRestOfDay();
        GrantMessage = Strings.Get("ScreenTime.GrantedRestOfDay");
        Refresh();
    }

    private void ResetToday()
    {
        _snapshot = _engine.ResetToday();
        GrantMessage = Strings.Get("ScreenTime.ResetDone");
        Refresh();
    }

    private static bool TryReadMinutes(object? parameter, out int minutes)
    {
        switch (parameter)
        {
            case int value:
                minutes = value;
                return true;
            case string text when int.TryParse(text, out var parsed):
                minutes = parsed;
                return true;
            default:
                minutes = 0;
                return false;
        }
    }

    // --------------------------------------------------------------- state

    public void Load(KidShellConfiguration draft)
    {
        _draft = draft;
        GrantMessage = null;
        Refresh();
    }

    /// <summary>Re-reads the engine and raises everything the page binds to.</summary>
    public void Refresh()
    {
        _snapshot = _engine.Evaluate();

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(WeekdayMinutes));
        OnPropertyChanged(nameof(WeekendMinutes));
        OnPropertyChanged(nameof(WeekdayText));
        OnPropertyChanged(nameof(WeekendText));
        OnPropertyChanged(nameof(RestrictHours));
        OnPropertyChanged(nameof(AllowedFromHour));
        OnPropertyChanged(nameof(AllowedUntilHour));
        OnPropertyChanged(nameof(HoursSummary));
        OnPropertyChanged(nameof(IsTrackingLive));
        OnPropertyChanged(nameof(HasUnsavedScreenTimeChange));
        OnPropertyChanged(nameof(UsedPercentage));
        OnPropertyChanged(nameof(UsedText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HasBonusToday));
        OnPropertyChanged(nameof(BonusText));
        OnPropertyChanged(nameof(ClockLooksTampered));
        OnPropertyChanged(nameof(Subtitle));
    }

    internal static string FormatDuration(int minutes) => minutes switch
    {
        0 => Strings.Format("ScreenTime.Minutes", 0),
        60 => Strings.Get("ScreenTime.OneHour"),
        < 60 => Strings.Format("ScreenTime.Minutes", minutes),
        _ when minutes % 60 == 0 => Strings.Format("ScreenTime.Hours", minutes / 60),
        _ => Strings.Format("ScreenTime.HoursAndMinutes", minutes / 60, minutes % 60)
    };
}
