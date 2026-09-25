using KidShell.Core.Runtime;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Profil.</summary>
public sealed partial class ParentProfilePage : UserControl
{
    private ParentProfileViewModel? _viewModel;
    private AboutViewModel? _about;
    private bool _loading;

    public ParentProfilePage() => InitializeComponent();

    public void Initialize(ParentProfileViewModel viewModel, AboutViewModel about)
    {
        // The preview moves beside or below the form; see ApplyLayout.
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);

        _viewModel = viewModel;
        _about = about;

        AvatarList.ItemsSource = viewModel.Avatars;
        ThemeBox.ItemsSource = viewModel.ThemeChoices;
        RerunOnboardingButton.Command = viewModel.RerunOnboardingCommand;

        viewModel.PropertyChanged += (_, _) => Render();
        about.PropertyChanged += (_, _) => RenderAbout();

        Render();
        RenderAbout();
    }

    /// <summary>
    /// Om KidShell. Rebuilt whenever the readiness report arrives, because the
    /// Windows facts are not known at construction time.
    /// </summary>
    private void RenderAbout()
    {
        if (_about is null)
        {
            return;
        }

        AboutFacts.ItemsSource = _about.Facts;

        DevelopmentWarning.Visibility = _about.IsDevelopmentBuild
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
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

    /// <summary>
    /// Puts the live preview beside the form, or below it.
    ///
    /// Two columns are worth having only while both are readable. At 780 the
    /// form and a 248-wide preview leave the form about 250 epx, which is
    /// narrower than the theme dropdown needs - so below that the preview
    /// moves under the form and both get the full width.
    /// </summary>
    private void ApplyLayout(double width)
    {
        if (width <= 0 || double.IsNaN(width))
        {
            return;
        }

        var stack = ResponsiveLayout.ShouldStack(width, panels: 2, minimumPanelWidth: 260);

        if (_stacked == stack)
        {
            return;
        }

        _stacked = stack;

        if (stack)
        {
            Grid.SetColumn(PreviewPanel, 0);
            Grid.SetRow(PreviewPanel, 1);
            PreviewColumn.Width = new GridLength(0);
            PreviewPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetColumn(PreviewPanel, 1);
            Grid.SetRow(PreviewPanel, 0);
            PreviewColumn.Width = GridLength.Auto;
            PreviewPanel.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private bool? _stacked;
}
