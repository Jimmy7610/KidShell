using KidShell.App.ViewModels;
using KidShell.Core.Apps;
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

        Opened += async (_, _) => await viewModel.LoadAsync();

        Render();
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
