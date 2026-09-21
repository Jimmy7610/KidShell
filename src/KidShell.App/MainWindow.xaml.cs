using System.ComponentModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.ViewModels;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI;

namespace KidShell.App;

/// <summary>
/// The KidShell window.
///
/// MVP 0.1 keeps an ordinary, closable, resizable window: developer mode is on
/// and there is deliberately nothing to trap anybody inside. The layering here
/// is the part that a later milestone turns into borderless full screen.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int InitialWidth = 1440;
    private const int InitialHeight = 900;

    private readonly ShellViewModel _viewModel;
    private readonly ISystemStatusService _status;
    private readonly IAppStateService _state;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _picker;
    private readonly IKidShellLogger _logger;

    public MainWindow(
        ShellViewModel viewModel,
        ISystemStatusService status,
        IAppStateService state,
        IDialogService dialogs,
        IFilePickerService picker,
        IKidShellLogger logger)
    {
        _viewModel = viewModel;
        _status = status;
        _state = state;
        _dialogs = dialogs;
        _picker = picker;
        _logger = logger;

        InitializeComponent();

        Title = Strings.Get("App.Title");

        ConfigureWindow();
        ConfigureTitleBar();
        WireViews();
        WireShortcuts();

        _status.Start();
        _state.ConfigurationChanged += OnConfigurationChanged;
        Closed += OnClosed;
        Activated += OnFirstActivated;

        Render();
    }

    private void ConfigureWindow()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        var width = Math.Min(InitialWidth, work.Width - 80);
        var height = Math.Min(InitialHeight, work.Height - 80);

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this the 4x2 grid stops being a comfortable read.
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 680;
        }

        AppWindow.SetIcon("Assets/KidShell.ico");
    }

    private void ConfigureTitleBar()
    {
        // The illustrated scene runs edge to edge; the caption buttons float
        // on top of it.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color.FromArgb(255, 22, 50, 79);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(160, 22, 50, 79);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(40, 22, 50, 79);
        titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 12, 32, 52);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(70, 22, 50, 79);
        titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 12, 32, 52);
    }

    private void WireViews()
    {
        _dialogs.Host = RootLayer;
        _picker.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ChildView.Initialize(
            _viewModel.Child,
            _viewModel.DeveloperMode,
            onSettingsRequested: () => _ = ShowChildSettingsNoticeAsync(),
            onParentAccessRequested: _viewModel.OpenPin);

        PinOverlay.Initialize(_viewModel.Pin);
        ParentView.Initialize(_viewModel.Parent, _status);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ExitRequested += (_, _) => Close();
    }

    private void WireShortcuts()
    {
        if (!_viewModel.DeveloperMode)
        {
            return;
        }

        // DEVELOPER ESCAPE HATCH: Ctrl+Shift+P opens the PIN prompt without
        // having to perform the three-second hold. Developer mode only.
        var accelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.P,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };

        accelerator.Invoked += (_, args) =>
        {
            _viewModel.OpenPin();
            args.Handled = true;
        };

        RootLayer.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        RootLayer.KeyboardAccelerators.Add(accelerator);
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        await _viewModel.ReportStartupIssuesAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void OnConfigurationChanged(object? sender, ConfigurationChangedEventArgs e) =>
        Scene.Theme = e.Configuration.Child.ThemeId;

    private void Render()
    {
        ChildView.Visibility = _viewModel.IsChildMode ? Visibility.Visible : Visibility.Collapsed;
        ParentView.Visibility = _viewModel.IsParentMode ? Visibility.Visible : Visibility.Collapsed;

        var pinVisible = _viewModel.IsPinOpen;
        PinOverlay.Visibility = pinVisible ? Visibility.Visible : Visibility.Collapsed;

        // Nothing behind the PIN gate should be reachable while it is up.
        ChildView.IsHitTestVisible = !pinVisible;
        ParentView.IsHitTestVisible = !pinVisible;

        if (pinVisible)
        {
            PinOverlay.PrepareForEntry();
        }

        Scene.Theme = _state.Current.Child.ThemeId;
    }

    private Task ShowChildSettingsNoticeAsync() =>
        _dialogs.ShowMessageAsync(
            Strings.Get("Child.Settings"),
            Strings.Get("Launch.NotConfiguredHint"),
            Strings.Get("Launch.Back"));

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _status.Stop();
        _state.ConfigurationChanged -= OnConfigurationChanged;
        _logger.Info("App", "KidShell closed.");
    }
}
