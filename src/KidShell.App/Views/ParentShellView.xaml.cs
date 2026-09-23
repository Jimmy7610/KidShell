using System.ComponentModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.ViewModels;
using KidShell.App.ViewModels.Parent;
using KidShell.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views;

/// <summary>Föräldraläge - navigation chrome plus the six pages.</summary>
public sealed partial class ParentShellView : UserControl
{
    private ParentShellViewModel? _viewModel;
    private bool _loading;

    public ParentShellView() => InitializeComponent();

    public void Initialize(ParentShellViewModel viewModel, ISystemStatusService status)
    {
        _viewModel = viewModel;

        Status.Initialize(status);

        PageOverview.Initialize(viewModel.Overview);
        PageApps.Initialize(viewModel.Apps);
        PageScreenTime.Initialize(viewModel.ScreenTime);
        PageWeb.Initialize(viewModel.Web);
        PageSecurity.Initialize(viewModel.Security, viewModel.Recovery);
        PageProfile.Initialize(viewModel.Profile, viewModel.About);

        SaveButton.Command = viewModel.SaveCommand;
        BackButton.Command = viewModel.BackCommand;
        ExitButton.Command = viewModel.ExitCommand;
        ChangePinButton.Command = viewModel.ChangePinCommand;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ScreenTime.PropertyChanged += (_, _) => RenderAside();
        viewModel.Web.PropertyChanged += (_, _) => RenderAside();

        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        _loading = true;

        ChildSummaryText.Text = _viewModel.ChildSummary;
        Avatar.AvatarId = _viewModel.AvatarId;

        SyncNavSelection();
        ShowPage();

        UnsavedBadge.Visibility = _viewModel.HasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;

        var message = _viewModel.StatusMessage;
        SavedBadge.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        StatusMessageText.Text = message ?? string.Empty;

        RenderAside();

        _loading = false;
    }

    private void RenderAside()
    {
        if (_viewModel is null)
        {
            return;
        }

        PinStatusText.Text = _viewModel.IsPinConfigured
            ? Strings.Get("Security.Active")
            : Strings.Get("Security.DevelopmentPinActive");

        AsideWeekdayText.Text = _viewModel.ScreenTime.WeekdayText;
        AsideWeekendText.Text = _viewModel.ScreenTime.WeekendText;
        AsideScreenTimeNotice.Text = _viewModel.ScreenTime.IsEnabled
            ? Strings.Get("Overview.ScreenTimeHint")
            : Strings.Get("Overview.ScreenTimeNoLimit");

        AsideWebText.Text = _viewModel.Web.IsAllowlist
            ? Strings.Get("Web.ModeAllowlist")
            : _viewModel.Web.IsOpenWeb
                ? Strings.Get("Web.ModeOpen")
                : Strings.Get("Web.ModeNone");
    }

    private void SyncNavSelection()
    {
        if (_viewModel is null)
        {
            return;
        }

        NavOverview.IsChecked = _viewModel.IsOverviewSelected;
        NavApps.IsChecked = _viewModel.IsAppsSelected;
        NavScreenTime.IsChecked = _viewModel.IsScreenTimeSelected;
        NavWeb.IsChecked = _viewModel.IsWebSelected;
        NavSecurity.IsChecked = _viewModel.IsSecuritySelected;
        NavProfile.IsChecked = _viewModel.IsProfileSelected;
    }

    private void ShowPage()
    {
        if (_viewModel is null)
        {
            return;
        }

        PageOverview.Visibility = Show(_viewModel.IsOverviewSelected);
        PageApps.Visibility = Show(_viewModel.IsAppsSelected);
        PageScreenTime.Visibility = Show(_viewModel.IsScreenTimeSelected);
        PageWeb.Visibility = Show(_viewModel.IsWebSelected);
        PageSecurity.Visibility = Show(_viewModel.IsSecuritySelected);
        PageProfile.Visibility = Show(_viewModel.IsProfileSelected);

        static Visibility Show(bool selected) => selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || _viewModel is null || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse<ParentPage>(tag, out var page))
        {
            _viewModel.SelectedPage = page;
        }
    }

    private void OnGoToScreenTime(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SelectedPage = ParentPage.ScreenTime;
        }
    }

    private void OnGoToWeb(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SelectedPage = ParentPage.Web;
        }
    }
}
