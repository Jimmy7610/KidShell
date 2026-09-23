using KidShell.App.Localization;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace KidShell.App.Views.Parent;

/// <summary>
/// Föräldraläge → Skärmtid.
///
/// The page renders from the view model rather than binding, so every value a
/// parent sees is produced by code that is unit-tested. The <c>_loading</c> flag
/// is the usual guard: assigning a Slider's Value raises ValueChanged, which
/// would otherwise write the value straight back and mark the draft dirty on
/// every render.
/// </summary>
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

        RenderToday();
        RenderHours();

        _loading = false;
    }

    private void RenderToday()
    {
        if (_viewModel is null)
        {
            return;
        }

        StatusText.Text = _viewModel.StatusSummary;
        UsageBar.Value = _viewModel.UsedPercentage;
        UsedText.Text = $"{Strings.Get("ScreenTime.Used")}: {_viewModel.UsedText}";
        RemainingText.Text = $"{Strings.Get("ScreenTime.Remaining")}: {_viewModel.RemainingText}";

        BonusText.Text = _viewModel.BonusText;
        BonusText.Visibility = _viewModel.HasBonusToday ? Visibility.Visible : Visibility.Collapsed;

        // Reported, never acted on: usage comes from a monotonic clock, so a
        // backward jump refunds nothing.
        ClockWarningText.Visibility = _viewModel.ClockLooksTampered ? Visibility.Visible : Visibility.Collapsed;

        GrantMessageText.Text = _viewModel.GrantMessage ?? string.Empty;
        GrantMessageText.Visibility = _viewModel.HasGrantMessage ? Visibility.Visible : Visibility.Collapsed;

        UnsavedNoticeText.Text = _viewModel.UnsavedNotice;
        UnsavedNotice.Visibility = _viewModel.HasUnsavedScreenTimeChange
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Granting time against an engine that is not counting would appear to
        // work and do nothing. The buttons follow the SAVED setting, not the
        // draft, because that is what the counter follows.
        var canGrant = _viewModel.IsTrackingLive;
        Extend15Button.IsEnabled = canGrant;
        Extend30Button.IsEnabled = canGrant;
        Extend60Button.IsEnabled = canGrant;
        ExtendRestButton.IsEnabled = canGrant;
        ResetTodayButton.IsEnabled = canGrant;
        TodayPanel.Opacity = canGrant ? 1 : 0.55;
    }

    private void RenderHours()
    {
        if (_viewModel is null)
        {
            return;
        }

        HoursToggle.IsOn = _viewModel.RestrictHours;
        FromSlider.Value = _viewModel.AllowedFromHour;
        UntilSlider.Value = _viewModel.AllowedUntilHour;
        FromValueText.Text = $"{(int)_viewModel.AllowedFromHour:00}:00";
        UntilValueText.Text = $"{(int)_viewModel.AllowedUntilHour:00}:00";
        HoursSummaryText.Text = _viewModel.HoursSummary;

        var active = _viewModel.IsEnabled && _viewModel.RestrictHours;
        FromSlider.IsEnabled = active;
        UntilSlider.IsEnabled = active;
        HoursControls.Opacity = active ? 1 : 0.55;
        HoursToggle.IsEnabled = _viewModel.IsEnabled;
        HoursPanel.Opacity = _viewModel.IsEnabled ? 1 : 0.55;
    }

    // ------------------------------------------------------------- handlers

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
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

    private void OnHoursToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.RestrictHours = HoursToggle.IsOn;
        }
    }

    private void OnFromChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.AllowedFromHour = e.NewValue;
        }
    }

    private void OnUntilChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading && _viewModel is not null)
        {
            _viewModel.AllowedUntilHour = e.NewValue;
        }
    }

    private void OnExtend15(object sender, RoutedEventArgs e) => Grant(15);

    private void OnExtend30(object sender, RoutedEventArgs e) => Grant(30);

    private void OnExtend60(object sender, RoutedEventArgs e) => Grant(60);

    private void Grant(int minutes) => _viewModel?.GrantExtensionCommand.Execute(minutes);

    private void OnExtendRestOfDay(object sender, RoutedEventArgs e) =>
        _viewModel?.GrantRestOfDayCommand.Execute(null);

    private void OnResetToday(object sender, RoutedEventArgs e) =>
        _viewModel?.ResetTodayCommand.Execute(null);
}
