using KidShell.App.Localization;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Skärmtid.</summary>
public sealed partial class ParentScreenTimePage : UserControl
{
    private ParentScreenTimeViewModel? _viewModel;
    private bool _loading;

    public ParentScreenTimePage() => InitializeComponent();

    public void Initialize(ParentScreenTimeViewModel viewModel)
    {
        _viewModel = viewModel;
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

        SubtitleText.Text = _viewModel.Subtitle;
        EnableToggle.IsOn = _viewModel.IsEnabled;
        WeekdaySlider.Value = _viewModel.WeekdayMinutes;
        WeekendSlider.Value = _viewModel.WeekendMinutes;
        WeekdayValueText.Text = _viewModel.WeekdayText;
        WeekendValueText.Text = _viewModel.WeekendText;

        EnabledStateText.Text = _viewModel.IsEnabled
            ? Strings.Get("Apps.EnabledTag")
            : Strings.Get("Apps.DisabledTag");

        // Limits stay visible when off, but read as inactive.
        LimitsPanel.Opacity = _viewModel.IsEnabled ? 1 : 0.55;
        WeekdaySlider.IsEnabled = _viewModel.IsEnabled;
        WeekendSlider.IsEnabled = _viewModel.IsEnabled;

        _loading = false;
    }

    private void OnEnabledToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.IsEnabled = EnableToggle.IsOn;
        }
    }

    private void OnWeekdayChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.WeekdayMinutes = e.NewValue;
        }
    }

    private void OnWeekendChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.WeekendMinutes = e.NewValue;
        }
    }
}
