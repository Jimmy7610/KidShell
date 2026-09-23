using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Mvvm;
using KidShell.Core.ScreenTime;

namespace KidShell.App.ViewModels;

/// <summary>
/// Barnläge. Everything on this screen comes from configuration, so a change
/// saved in Parent Mode is reflected here as soon as it is committed.
///
/// SCREEN TIME, AS A CHILD EXPERIENCES IT
/// --------------------------------------
/// The allowance is not a countdown clock in the corner: watching a number fall
/// is stressful, and a six-year-old cannot read it anyway. Instead the child
/// gets a gentle line when time is short, a calm full-screen message when it
/// runs out, and nothing at all the rest of the time.
///
/// Time up does not close anything or hide the screen. It stops KidShell
/// launching anything new and says, in words a child can read, that they should
/// go and find an adult. Slamming the screen off mid-drawing would teach a
/// child to distrust the machine.
/// </summary>
public sealed class ChildHomeViewModel : ObservableObject
{
    private readonly IAppStateService _state;
    private readonly IAppLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly IScreenTimeCoordinator _screenTime;

    private string _greeting = string.Empty;
    private string _avatarId = "fox";
    private bool _hasTiles;
    private ScreenTimeStatusView _status;

    public ChildHomeViewModel(
        IAppStateService state,
        IAppLauncher launcher,
        IDialogService dialogs,
        ISystemStatusService status,
        IScreenTimeCoordinator screenTime)
    {
        _state = state;
        _launcher = launcher;
        _dialogs = dialogs;
        _screenTime = screenTime;
        Status = status;

        _status = screenTime.Current;

        LaunchCommand = new RelayCommand(parameter => _ = LaunchAsync(parameter as ChildAppTileViewModel));
        DismissWarningCommand = new RelayCommand(DismissWarning);

        _state.ConfigurationChanged += (_, _) => Refresh();
        _screenTime.Changed += (_, view) => OnScreenTimeChanged(view);

        Refresh();
    }

    public ISystemStatusService Status { get; }

    public ObservableCollection<ChildAppTileViewModel> Tiles { get; } = [];

    public RelayCommand LaunchCommand { get; }

    public RelayCommand DismissWarningCommand { get; }

    public string Greeting
    {
        get => _greeting;
        private set => SetProperty(ref _greeting, value);
    }

    public string Encouragement => Strings.Get("Child.Encouragement");

    public string AvatarId
    {
        get => _avatarId;
        private set => SetProperty(ref _avatarId, value);
    }

    public bool HasTiles
    {
        get => _hasTiles;
        private set => SetProperty(ref _hasTiles, value);
    }

    // ----------------------------------------------------------- screen time

    /// <summary>Whether the calm time-is-up screen covers the grid.</summary>
    public bool IsTimeUp => _state.Current.ScreenTime.IsEnabled && _status.IsBlocked;

    /// <summary>The headline on the time-is-up screen.</summary>
    public string TimeUpTitle => _status.Snapshot.Status == ScreenTimeStatus.OutsideAllowedHours
        ? Strings.Get("Child.OutsideHoursTitle")
        : Strings.Get("Child.TimeUpTitle");

    public string TimeUpBody => _status.Snapshot.Status == ScreenTimeStatus.OutsideAllowedHours
        ? Strings.Get("Child.OutsideHoursBody")
        : Strings.Get("Child.TimeUpBody");

    /// <summary>Whether a "time is nearly up" line is showing.</summary>
    public bool HasWarning => !IsTimeUp && _status.NewWarningMinutes is not null;

    public string WarningText => _status.NewWarningMinutes switch
    {
        null => string.Empty,
        1 => Strings.Get("Child.WarningOneMinute"),
        var minutes => Strings.Format("Child.WarningMinutes", minutes)
    };

    private void OnScreenTimeChanged(ScreenTimeStatusView view)
    {
        _status = view;

        OnPropertyChanged(nameof(IsTimeUp));
        OnPropertyChanged(nameof(TimeUpTitle));
        OnPropertyChanged(nameof(TimeUpBody));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningText));
    }

    private void DismissWarning()
    {
        if (_status.NewWarningMinutes is { } minutes)
        {
            // Acknowledged so it is not shown again every tick. The next
            // threshold still will be.
            _screenTime.AcknowledgeWarning(minutes);
        }

        _status = _status with { NewWarningMinutes = null };
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningText));
    }

    // ---------------------------------------------------------------- state

    public void Refresh()
    {
        var config = _state.Current;

        Greeting = Strings.Format("Child.Greeting", config.Child.Name);
        AvatarId = config.Child.AvatarId;

        Tiles.Clear();
        foreach (var app in config.EnabledApps)
        {
            Tiles.Add(new ChildAppTileViewModel(app));
        }

        HasTiles = Tiles.Count > 0;

        // A parent may have granted more time or changed the allowance while
        // this screen was hidden.
        _screenTime.Refresh();
        OnScreenTimeChanged(_screenTime.Current);
    }

    private async Task LaunchAsync(ChildAppTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (!_screenTime.CanLaunch())
        {
            // Checked here rather than only hiding the tiles: the grid may have
            // been on screen when the allowance ran out, and a tap already in
            // flight must not slip through.
            await _dialogs.ShowMessageAsync(
                Strings.Get("Child.TimeUpTitle"),
                Strings.Get("Child.TimeUpBody"));

            OnPropertyChanged(nameof(IsTimeUp));
            return;
        }

        var result = _launcher.Launch(tile.Definition);

        if (!result.IsSuccess)
        {
            // Friendly wording for the child; the technical reason is already
            // in the log.
            await _dialogs.ShowLaunchProblemAsync(result);
        }
    }
}
