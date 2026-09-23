using System.ComponentModel;
using KidShell.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace KidShell.App.Views;

/// <summary>First-run setup: welcome, name, avatar, age, theme, finish.</summary>
public sealed partial class OnboardingView : UserControl
{
    private OnboardingViewModel? _viewModel;
    private OnboardingStep _lastStep = OnboardingStep.Welcome;
    private bool _loading;

    public OnboardingView() => InitializeComponent();

    public void Initialize(OnboardingViewModel viewModel)
    {
        _viewModel = viewModel;

        AvatarGrid.ItemsSource = viewModel.Avatars;
        AgeGrid.ItemsSource = viewModel.Ages;
        ThemeGrid.ItemsSource = viewModel.Themes;

        BackButton.Command = viewModel.BackCommand;
        PrimaryButton.Command = viewModel.ContinueCommand;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.StepChanged += OnStepChanged;

        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void OnStepChanged(object? sender, OnboardingStep step)
    {
        // Short, directional, and out of the way: forward slides in from the
        // right, Back from the left.
        var goingBack = step < _lastStep;
        _lastStep = step;

        Render();
        FocusStep(step);

        if (goingBack)
        {
            StepEnterBack.Begin();
        }
        else
        {
            StepEnter.Begin();
        }
    }

    private void FocusStep(OnboardingStep step)
    {
        if (step == OnboardingStep.Name)
        {
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectionStart = NameBox.Text.Length;
        }
        else
        {
            PrimaryButton.Focus(FocusState.Programmatic);
        }
    }

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        _loading = true;

        StepWelcome.Visibility = Show(_viewModel.IsWelcome);
        StepParentPin.Visibility = Show(_viewModel.IsParentPin);
        StepName.Visibility = Show(_viewModel.IsName);
        StepAvatar.Visibility = Show(_viewModel.IsAvatar);
        StepAge.Visibility = Show(_viewModel.IsAge);
        StepTheme.Visibility = Show(_viewModel.IsTheme);
        StepRules.Visibility = Show(_viewModel.IsRules);
        StepDone.Visibility = Show(_viewModel.IsDone);

        StepIndicatorText.Text = _viewModel.StepIndicator;
        StepIndicatorText.Visibility = Show(_viewModel.ShowsStepIndicator);

        BackButton.Visibility = Show(_viewModel.CanGoBack);
        PrimaryButton.Content = _viewModel.PrimaryButtonText;

        if (NameBox.Text != _viewModel.NameText)
        {
            NameBox.Text = _viewModel.NameText;
        }

        AvatarTitleText.Text = _viewModel.AvatarTitle;
        AvatarBodyText.Text = _viewModel.AvatarBody;
        AgeTitleText.Text = _viewModel.AgeTitle;
        ThemeBodyText.Text = _viewModel.ThemeBody;
        DoneTitleText.Text = _viewModel.DoneTitle;
        DoneSummaryText.Text = _viewModel.DoneSummary;
        DoneRulesText.Text = _viewModel.RulesSummary;
        DoneAvatar.AvatarId = _viewModel.SelectedAvatarId;

        PinBodyText.Text = _viewModel.PinBody;

        // The PIN boxes are never written back from the view model. A PIN
        // sitting in a field is a PIN somebody can read over a shoulder, and
        // stepping back deliberately clears them so the parent retypes - which
        // re-confirms it too.

        RulesScreenTimeToggle.IsOn = _viewModel.ScreenTimeEnabled;
        RulesWeekdaySlider.Value = _viewModel.WeekdayMinutes;
        RulesWeekendSlider.Value = _viewModel.WeekendMinutes;
        RulesWeekdayText.Text = _viewModel.WeekdayText;
        RulesWeekendText.Text = _viewModel.WeekendText;

        RulesTimeControls.Opacity = _viewModel.ScreenTimeEnabled ? 1 : 0.55;
        RulesWeekdaySlider.IsEnabled = _viewModel.ScreenTimeEnabled;
        RulesWeekendSlider.IsEnabled = _viewModel.ScreenTimeEnabled;

        RulesWebNone.IsChecked = _viewModel.WebNone;
        RulesWebAllowlist.IsChecked = _viewModel.WebAllowlist;
        RulesWebOpen.IsChecked = _viewModel.WebOpen;

        var message = _viewModel.ValidationMessage;
        ValidationPanel.Visibility = Show(!string.IsNullOrEmpty(message));
        ValidationText.Text = message ?? string.Empty;

        _loading = false;

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------ parent PIN

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.PinText = PinBox.Password;
        }
    }

    private void OnPinConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.PinConfirmText = PinConfirmBox.Password;
        }
    }

    // ----------------------------------------------------------- rules

    private void OnRulesScreenTimeToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.ScreenTimeEnabled = RulesScreenTimeToggle.IsOn;
        }
    }

    private void OnRulesWeekdayChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.WeekdayMinutes = e.NewValue;
        }
    }

    private void OnRulesWeekendChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.WeekendMinutes = e.NewValue;
        }
    }

    private void OnRulesWebChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || _viewModel is null)
        {
            return;
        }

        if (ReferenceEquals(sender, RulesWebNone))
        {
            _viewModel.WebNone = true;
        }
        else if (ReferenceEquals(sender, RulesWebAllowlist))
        {
            _viewModel.WebAllowlist = true;
        }
        else if (ReferenceEquals(sender, RulesWebOpen))
        {
            _viewModel.WebOpen = true;
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.NameText = NameBox.Text;
        }
    }

    private void OnAvatarClick(object sender, RoutedEventArgs e) =>
        Invoke<OnboardingAvatarViewModel>(sender, choice => _viewModel?.SelectAvatarCommand.Execute(choice));

    private void OnAgeClick(object sender, RoutedEventArgs e) =>
        Invoke<OnboardingAgeViewModel>(sender, choice => _viewModel?.SelectAgeCommand.Execute(choice));

    private void OnThemeClick(object sender, RoutedEventArgs e) =>
        Invoke<OnboardingThemeViewModel>(sender, choice => _viewModel?.SelectThemeCommand.Execute(choice));

    /// <summary>
    /// ItemsRepeater does not set DataContext on realized elements, so the
    /// item travels on Tag; DataContext is only a fallback.
    /// </summary>
    private static void Invoke<T>(object sender, Action<T> action)
        where T : class
    {
        var item = (sender as FrameworkElement)?.Tag as T
                   ?? (sender as FrameworkElement)?.DataContext as T;

        if (item is not null)
        {
            action(item);
        }
    }
}
