using KidShell.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views.Dialogs;

/// <summary>"Lägg till app" - manual configuration of an extra program.</summary>
public sealed partial class AddAppDialog : ContentDialog
{
    private readonly AddAppViewModel _viewModel;
    private bool _loading;

    public AddAppDialog(AddAppViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        IconBox.ItemsSource = viewModel.Icons;
        AccentBox.ItemsSource = viewModel.Accents;
        BrowseButton.Command = viewModel.BrowseCommand;
        RequestedTheme = ElementTheme.Light;

        viewModel.PropertyChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        _loading = true;

        if (NameBox.Text != _viewModel.DisplayName)
        {
            NameBox.Text = _viewModel.DisplayName;
        }

        if (ProgramBox.Text != _viewModel.ProgramName)
        {
            ProgramBox.Text = _viewModel.ProgramName;
        }

        if (PathBox.Text != _viewModel.ExecutablePath)
        {
            PathBox.Text = _viewModel.ExecutablePath;
        }

        if (ArgumentsBox.Text != _viewModel.Arguments)
        {
            ArgumentsBox.Text = _viewModel.Arguments;
        }

        if (CategoryBox.Text != _viewModel.Category)
        {
            CategoryBox.Text = _viewModel.Category;
        }

        IconBox.SelectedIndex = _viewModel.SelectedIconIndex;
        AccentBox.SelectedIndex = _viewModel.SelectedAccentIndex;

        var message = _viewModel.ValidationMessage;
        ValidationPanel.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ValidationText.Text = message ?? string.Empty;

        _loading = false;
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e) => PushToViewModel();

    private void OnFieldSelectionChanged(object sender, SelectionChangedEventArgs e) => PushToViewModel();

    private void PushToViewModel()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.DisplayName = NameBox.Text;
        _viewModel.ProgramName = ProgramBox.Text;
        _viewModel.ExecutablePath = PathBox.Text;
        _viewModel.Arguments = ArgumentsBox.Text;
        _viewModel.Category = CategoryBox.Text;

        if (IconBox.SelectedIndex >= 0)
        {
            _viewModel.SelectedIconIndex = IconBox.SelectedIndex;
        }

        if (AccentBox.SelectedIndex >= 0)
        {
            _viewModel.SelectedAccentIndex = AccentBox.SelectedIndex;
        }
    }
}
