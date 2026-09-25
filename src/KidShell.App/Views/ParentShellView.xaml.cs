using System.ComponentModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.ViewModels;
using KidShell.App.ViewModels.Parent;
using KidShell.Core.Configuration;
using KidShell.Core.Runtime;
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

        // The layout follows the space actually available, not the size the
        // window happened to open at.
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);

        Render();
    }

    /// <summary>
    /// The last layout applied, so a resize that does not cross a boundary
    /// costs nothing.
    /// </summary>
    private NavigationMode? _navigationMode;
    private bool? _asideVisible;

    /// <summary>
    /// Adapts the chrome to the width available.
    ///
    /// Three things give way, in this order, because that is the order in
    /// which they stop earning their space:
    ///
    ///  1. the at-a-glance column, which is a convenience - everything in it
    ///     is reachable from a page;
    ///  2. the navigation labels, leaving an icon rail;
    ///  3. nothing else. The content column never shrinks below what a
    ///     settings row needs; if the window is smaller than that, the page
    ///     scrolls.
    ///
    /// The decisions come from ResponsiveLayout so they are tested against the
    /// whole supported matrix rather than against one monitor.
    /// </summary>
    private void ApplyLayout(double width)
    {
        if (width <= 0 || double.IsNaN(width))
        {
            return;
        }

        var aside = ResponsiveLayout.ShowAside(width);

        if (_asideVisible != aside)
        {
            _asideVisible = aside;
            Aside.Visibility = aside ? Visibility.Visible : Visibility.Collapsed;

            // Collapse the column too, not just its content: a hidden element
            // in a fixed-width column still reserves the width.
            AsideColumn.Width = aside
                ? new GridLength(ResponsiveLayout.AsideWidth)
                : new GridLength(0);
        }

        var navigation = ResponsiveLayout.DecideNavigation(width);

        if (_navigationMode != navigation)
        {
            _navigationMode = navigation;
            ApplyNavigationMode(navigation);
        }

        // Decoration gives way before anything functional does. The footer
        // wordmark was sitting underneath the action buttons rather than
        // beside them, and the header tagline pushed the child's name into the
        // status strip.
        var roomy = ResponsiveLayout.Classify(width) != LayoutSize.Compact;

        FooterBrand.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        HeaderTagline.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        HeaderChildText.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyNavigationMode(NavigationMode mode)
    {
        var expanded = mode == NavigationMode.Expanded;

        NavigationPanel.Width = expanded
            ? ResponsiveLayout.ExpandedNavigationWidth
            : ResponsiveLayout.RailNavigationWidth;

        // The labels go, the icons stay. Each button keeps an automation name
        // and a tooltip, so the rail is still announceable and still
        // explains itself on hover.
        var labelVisibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        foreach (var label in new[]
                 {
                     NavLabelOverview, NavLabelApps, NavLabelScreenTime,
                     NavLabelWeb, NavLabelSecurity, NavLabelProfile
                 })
        {
            label.Visibility = labelVisibility;
        }

        // Centre the glyph with symmetric padding rather than by changing the
        // content alignment.
        //
        // HorizontalContentAlignment="Center" was the obvious lever and the
        // wrong one: it makes the content presenter size to its content and
        // centre it, so the button's own row - icon plus the collapsed label's
        // spacing - ended up wider than the rail and was clipped, taking the
        // icons with it.
        //
        // Stretch keeps the row anchored at the left edge, and the padding
        // does the centring: 72 wide, less the panel's 24 of padding, leaves
        // 48 for a 26-wide glyph, so 11 either side.
        const double railPadding = (RailNavigationInnerWidth - NavigationGlyphWidth) / 2;

        var padding = expanded
            ? new Thickness(16, 10, 16, 10)
            : new Thickness(railPadding, 10, railPadding, 10);

        foreach (var button in new[]
                 {
                     NavOverview, NavApps, NavScreenTime,
                     NavWeb, NavSecurity, NavProfile
                 })
        {
            button.Padding = padding;
        }
    }

    /// <summary>The rail's width less the panel's own padding.</summary>
    private const double RailNavigationInnerWidth = ResponsiveLayout.RailNavigationWidth - 24;

    /// <summary>Matches NavigationGlyphStyle's Width.</summary>
    private const double NavigationGlyphWidth = 26;

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
