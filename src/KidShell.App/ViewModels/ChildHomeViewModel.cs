using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels;

/// <summary>
/// Barnläge. Everything on this screen comes from configuration, so a change
/// saved in Parent Mode is reflected here as soon as it is committed.
/// </summary>
public sealed class ChildHomeViewModel : ObservableObject
{
    private readonly IAppStateService _state;
    private readonly IAppLauncher _launcher;
    private readonly IDialogService _dialogs;

    private string _greeting = string.Empty;
    private string _avatarId = "fox";
    private bool _hasTiles;

    public ChildHomeViewModel(
        IAppStateService state,
        IAppLauncher launcher,
        IDialogService dialogs,
        ISystemStatusService status)
    {
        _state = state;
        _launcher = launcher;
        _dialogs = dialogs;
        Status = status;

        LaunchCommand = new RelayCommand(parameter => _ = LaunchAsync(parameter as ChildAppTileViewModel));

        _state.ConfigurationChanged += (_, _) => Refresh();
        Refresh();
    }

    public ISystemStatusService Status { get; }

    public ObservableCollection<ChildAppTileViewModel> Tiles { get; } = [];

    public RelayCommand LaunchCommand { get; }

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
    }

    private async Task LaunchAsync(ChildAppTileViewModel? tile)
    {
        if (tile is null)
        {
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
