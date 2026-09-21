using KidShell.App.Localization;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels.Parent;

/// <summary>
/// Föräldraläge → Skärmtid.
///
/// MVP 0.1 stores these values and says so plainly on the page: nothing here
/// interrupts the child yet.
/// </summary>
public sealed class ParentScreenTimeViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();

    public ParentScreenTimeViewModel(Action onChanged) => _onChanged = onChanged;

    public string Subtitle => Strings.Format("ScreenTime.Subtitle", _draft.Child.Name);

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

    public void Load(KidShellConfiguration draft)
    {
        _draft = draft;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(WeekdayMinutes));
        OnPropertyChanged(nameof(WeekendMinutes));
        OnPropertyChanged(nameof(WeekdayText));
        OnPropertyChanged(nameof(WeekendText));
        OnPropertyChanged(nameof(Subtitle));
    }

    internal static string FormatDuration(int minutes) => minutes switch
    {
        60 => Strings.Get("ScreenTime.OneHour"),
        < 60 => Strings.Format("ScreenTime.Minutes", minutes),
        _ when minutes % 60 == 0 => Strings.Format("ScreenTime.Hours", minutes / 60),
        _ => Strings.Format("ScreenTime.HoursAndMinutes", minutes / 60, minutes % 60)
    };
}
