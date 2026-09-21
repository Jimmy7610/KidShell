using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Profil.</summary>
public sealed partial class ParentProfilePage : UserControl
{
    private ParentProfileViewModel? _viewModel;
    private bool _loading;

    public ParentProfilePage() => InitializeComponent();

    public void Initialize(ParentProfileViewModel viewModel)
    {
        _viewModel = viewModel;
        AvatarList.ItemsSource = viewModel.Avatars;
        ThemeBox.ItemsSource = viewModel.ThemeChoices;
        viewModel.PropertyChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        _loading = true;

        if (NameBox.Text != _viewModel.Name)
        {
            NameBox.Text = _viewModel.Name;
        }

        AgeSlider.Value = _viewModel.Age;
        AgeValueText.Text = $"{(int)_viewModel.Age} år";
        ThemeBox.SelectedIndex = _viewModel.SelectedThemeIndex;
        PreviewAvatar.AvatarId = _viewModel.AvatarId;
        PreviewGreetingText.Text = _viewModel.PreviewGreeting;
        PreviewSummaryText.Text = _viewModel.PreviewSummary;

        _loading = false;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.Name = NameBox.Text;
        }
    }

    private void OnAgeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.Age = e.NewValue;
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null && ThemeBox.SelectedIndex >= 0)
        {
            _viewModel.SelectedThemeIndex = ThemeBox.SelectedIndex;
        }
    }

    private void OnAvatarClick(object sender, RoutedEventArgs e)
    {
        var choice = (sender as FrameworkElement)?.Tag as AvatarChoiceViewModel
                     ?? (sender as FrameworkElement)?.DataContext as AvatarChoiceViewModel;

        if (choice is not null)
        {
            _viewModel?.SelectAvatarCommand.Execute(choice);
        }
    }
}
