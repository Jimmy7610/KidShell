using KidShell.App.ViewModels;
using KidShell.Core.Apps;
using KidShell.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views.Dialogs;

/// <summary>
/// The installed-applications browser.
///
/// The dialog closes itself when the parent picks an application, so the flow
/// is one tap rather than "select, then press OK". <see cref="Chosen"/> carries
/// the result out; a null means they cancelled or asked for the manual form.
/// </summary>
public sealed partial class BrowseAppsDialog : ContentDialog
{
    private readonly AppBrowserViewModel _viewModel;

    public BrowseAppsDialog(AppBrowserViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        ResultList.ItemsSource = viewModel.Results;
        RefreshButton.Command = viewModel.RefreshCommand;

        viewModel.PropertyChanged += (_, _) => Render();
        viewModel.Results.CollectionChanged += (_, _) => Render();

        Opened += async (_, _) =>
        {
            ApplySize();
            await viewModel.LoadAsync();
        };

        Render();
    }

    /// <summary>
    /// Sizes the dialog from the window rather than from a constant.
    ///
    /// A fixed width clips its own buttons on a narrow window; a fixed list
    /// height hides rows on a short one with no visible scrollbar to suggest
    /// anything is missing. Both have happened here.
    /// </summary>
    /// <summary>
    /// The dialog's own cap and padding, read from the theme rather than
    /// copied into a constant. See ResponsiveLayout.DialogContentWidth.
    /// </summary>
    private static (double Cap, double Padding) DialogMetrics()
    {
        var cap = 548d;
        var padding = 52d;

        if (Application.Current?.Resources.TryGetValue("ContentDialogMaxWidth", out var capValue) is true &&
            capValue is double themeCap)
        {
            cap = themeCap;
        }

        if (Application.Current?.Resources.TryGetValue("ContentDialogPadding", out var padValue) is true &&
            padValue is Thickness themePadding)
        {
            padding = themePadding.Left + themePadding.Right;
        }

        return (cap, padding);
    }

    private void ApplySize()
    {
        var bounds = XamlRoot?.Size ?? default;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            // No XamlRoot yet: leave it to size to content rather than guess.
            return;
        }

        // Size the content to fit inside the dialog's own cap.
        //
        // Two wrong answers came first. A fixed 560 clipped the Add buttons at
        // 1366. Sizing the inner Grid to 520 clipped them again, because
        // ContentDialog caps itself at ContentDialogMaxWidth - a theme
        // resource of 548 - and 520 plus the dialog's 24-epx padding either
        // side exceeds it. Setting MinWidth and MaxWidth on the dialog itself
        // fixed the clipping and broke the centring, because that is what the
        // dialog's own layout uses to centre.
        //
        // So: leave the dialog alone and ask for content that fits within its
        // cap. DialogContentWidth is that cap less the padding.
        var (cap, padding) = DialogMetrics();

        DialogRoot.Width = ResponsiveLayout.DialogContentWidth(bounds.Width, cap, padding);

        // Chrome: the dialog's title, the search row, the summary and the
        // command bar, plus the dimmed margin the dialog sits in.
        ResultArea.Height = ResponsiveLayout.ScrollableHeight(
            bounds.Height, reservedForChrome: 320, minimum: 180, maximum: 520);

        // The floor above is a floor, and at 640x480 with 200% text the chrome
        // it is measured against is far larger than 320 - so the content came
        // to 59 epx more than the dialog could draw, and the summary and the
        // note underneath the list were simply not there. The whole content
        // scrolls when that happens, which is the one answer that does not
        // involve hiding something.
        DialogScroller.MaxHeight = ResponsiveLayout.ScrollableHeight(
            bounds.Height, reservedForChrome: 150, minimum: 160, maximum: 900);
    }

    /// <summary>
    /// The application the parent picked, if they picked one.
    ///
    /// Internal: a public property of this type on a XAML-rooted class makes
    /// the XAML compiler generate an activator for DiscoveredApplication, which
    /// has required members and cannot be default-constructed.
    /// </summary>
    internal DiscoveredApplication? Chosen { get; private set; }

    private void Render()
    {
        LoadingPanel.Visibility = _viewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ResultScroller.Visibility = _viewModel.IsLoading ? Visibility.Collapsed : Visibility.Visible;

        EmptyText.Text = _viewModel.EmptyStateText;
        EmptyText.Visibility = _viewModel.ShowEmptyState ? Visibility.Visible : Visibility.Collapsed;

        SummaryText.Text = _viewModel.ResultSummary;

        ErrorText.Text = _viewModel.ErrorMessage ?? string.Empty;
        ErrorText.Visibility = _viewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        _viewModel.Query = SearchBox.Text;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.Tag as DiscoveredAppViewModel
                  ?? (sender as FrameworkElement)?.DataContext as DiscoveredAppViewModel;

        if (row is not { CanAdd: true })
        {
            return;
        }

        Chosen = row.Application;

        // Closing here rather than making the parent confirm: they pressed the
        // button on the row they wanted.
        Hide();
    }
}
