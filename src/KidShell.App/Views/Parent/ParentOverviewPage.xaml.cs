using System.ComponentModel;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Översikt.</summary>
public sealed partial class ParentOverviewPage : UserControl
{
    private ParentOverviewViewModel? _viewModel;

    public ParentOverviewPage() => InitializeComponent();

    public void Initialize(ParentOverviewViewModel viewModel)
    {
        _viewModel = viewModel;
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

        SubtitleText.Text = _viewModel.Subtitle;
        AppsValueText.Text = _viewModel.AppsValue;
        AppsHintText.Text = _viewModel.AppsHint;
        ScreenTimeValueText.Text = _viewModel.ScreenTimeValue;
        WebValueText.Text = _viewModel.WebValue;
        SecurityValueText.Text = _viewModel.SecurityValue;
    }
}
