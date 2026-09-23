using System.ComponentModel;
using KidShell.App.Localization;
using KidShell.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views;

/// <summary>Barnläge - the screen the child actually lives on.</summary>
public sealed partial class ChildHomeView : UserControl
{
    private ChildHomeViewModel? _viewModel;
    private Action? _onSettingsRequested;
    private Action? _onParentAccessRequested;

    public ChildHomeView() => InitializeComponent();

    public void Initialize(
        ChildHomeViewModel viewModel,
        bool developerMode,
        Action onSettingsRequested,
        Action onParentAccessRequested)
    {
        _viewModel = viewModel;
        _onSettingsRequested = onSettingsRequested;
        _onParentAccessRequested = onParentAccessRequested;

        Status.Initialize(viewModel.Status);
        AppGrid.ItemsSource = viewModel.Tiles;
        viewModel.Tiles.CollectionChanged += (_, _) => UpdateEmptyState();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        DeveloperBadge.Visibility = developerMode ? Visibility.Visible : Visibility.Collapsed;

        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        GreetingText.Text = _viewModel.Greeting;
        Avatar.AvatarId = _viewModel.AvatarId;

        // Rendered every time, not captured at startup. Name the state that
        // actually matters: "development build" is a detail, "the PIN printed
        // in the README opens Parent Mode right now" is what somebody needs to
        // notice - and it stops being true the moment a parent sets a real one.
        DeveloperBadgeText.Text = _viewModel.DevelopmentPinActive
            ? Localization.Strings.Get("Dev.BadgeOpenPin")
            : Localization.Strings.Get("Dev.Badge");

        RenderScreenTime();
        UpdateEmptyState();
    }

    private void RenderScreenTime()
    {
        if (_viewModel is null)
        {
            return;
        }

        TimeUpTitleText.Text = _viewModel.TimeUpTitle;
        TimeUpBodyText.Text = _viewModel.TimeUpBody;
        TimeUpState.Visibility = _viewModel.IsTimeUp ? Visibility.Visible : Visibility.Collapsed;

        WarningText.Text = _viewModel.WarningText;
        WarningBanner.Visibility = _viewModel.HasWarning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDismissWarning(object sender, RoutedEventArgs e) =>
        _viewModel?.DismissWarningCommand.Execute(null);

    private void UpdateEmptyState()
    {
        // Three states share this space and only one may show. Time up wins:
        // "no apps are switched on" is true but unhelpful when the real reason
        // the grid is gone is that the day's time has run out.
        var timeUp = _viewModel?.IsTimeUp ?? false;
        var hasTiles = _viewModel?.Tiles.Count > 0;

        EmptyState.Visibility = !timeUp && !hasTiles ? Visibility.Visible : Visibility.Collapsed;
        GridScroller.Visibility = !timeUp && hasTiles ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        // Tag is set from the item template; DataContext is the fallback.
        var tile = (sender as FrameworkElement)?.Tag as ChildAppTileViewModel
                   ?? (sender as FrameworkElement)?.DataContext as ChildAppTileViewModel;

        if (tile is not null)
        {
            _viewModel?.LaunchCommand.Execute(tile);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _onSettingsRequested?.Invoke();

    private void OnParentAccessHeld(object? sender, EventArgs e) => _onParentAccessRequested?.Invoke();

    /// <summary>Moves keyboard focus onto the first card when Child Mode appears.</summary>
    public void FocusFirstTile()
    {
        if (_viewModel?.Tiles.Count > 0)
        {
            AppGrid.TryGetElement(0)?.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Title used by assistive technology for the whole screen.</summary>
    public string ScreenName => Strings.Get("Child.Wordmark");
}
