using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Säkerhet.</summary>
public sealed partial class ParentSecurityPage : UserControl
{
    public ParentSecurityPage() => InitializeComponent();

    public void Initialize(ParentSecurityViewModel viewModel)
    {
        StatusList.ItemsSource = viewModel.Statuses;
        ConfigPathText.Text = viewModel.ConfigurationPath;
        LogPathText.Text = viewModel.LogPath;
    }
}
