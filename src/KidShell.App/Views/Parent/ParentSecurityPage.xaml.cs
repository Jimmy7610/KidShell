using System.ComponentModel;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views.Parent;

/// <summary>
/// Föräldraläge → Säkerhet.
///
/// Read-only throughout: the buttons open informational dialogs and re-run the
/// same detection. Nothing here writes a Windows setting.
/// </summary>
public sealed partial class ParentSecurityPage : UserControl
{
    private ParentSecurityViewModel? _viewModel;

    public ParentSecurityPage() => InitializeComponent();

    public void Initialize(ParentSecurityViewModel viewModel)
    {
        _viewModel = viewModel;

        StatusList.ItemsSource = viewModel.Statuses;
        CapabilityList.ItemsSource = viewModel.Capabilities;
        CheckList.ItemsSource = viewModel.Checks;
        WindowsFactList.ItemsSource = viewModel.WindowsFacts;
        DiagnosticsList.ItemsSource = viewModel.Diagnostics;
        BlockerList.ItemsSource = viewModel.Blockers;
        WarningList.ItemsSource = viewModel.Warnings;

        RescanButton.Command = viewModel.RescanCommand;
        CompareButton.Command = viewModel.ShowComparisonCommand;
        PlanButton.Command = viewModel.ShowPlanCommand;

        ConfigPathText.Text = viewModel.ConfigurationPath;
        LogPathText.Text = viewModel.LogPath;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        StatusHeadlineText.Text = _viewModel.StatusHeadline;
        StatusBodyText.Text = _viewModel.StatusBody;
        LockNoticeText.Text = _viewModel.LockNotice;

        RecommendedModeText.Text = _viewModel.RecommendedModeTitle;
        RecommendedModeBodyText.Text = _viewModel.RecommendedModeBody;

        BlockerPanel.Visibility = _viewModel.HasBlockers ? Visibility.Visible : Visibility.Collapsed;
        WarningPanel.Visibility = _viewModel.HasWarnings ? Visibility.Visible : Visibility.Collapsed;

        RescanButton.IsEnabled = !_viewModel.IsScanning;
    }
}
