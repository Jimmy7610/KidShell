using System.ComponentModel;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Appar.</summary>
public sealed partial class ParentAppsPage : UserControl
{
    private ParentAppsViewModel? _viewModel;

    public ParentAppsPage() => InitializeComponent();

    public void Initialize(ParentAppsViewModel viewModel)
    {
        _viewModel = viewModel;
        AppRows.ItemsSource = viewModel.Rows;
        AddAppButton.Command = viewModel.AddAppCommand;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_viewModel is not null)
        {
            SubtitleText.Text = _viewModel.Subtitle;
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var row = (sender as FrameworkElement)?.Tag as ParentAppRowViewModel
                  ?? (sender as FrameworkElement)?.DataContext as ParentAppRowViewModel;

        if (row is not null)
        {
            _viewModel?.RemoveCommand.Execute(row);
        }
    }
}
