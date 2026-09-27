using System.ComponentModel;
using System.Globalization;
using KidShell.App.Localization;
using KidShell.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views;

/// <summary>Time, network and battery readout shared by both modes.</summary>
public sealed partial class StatusStrip : UserControl
{
    private ISystemStatusService? _status;

    public StatusStrip()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, e) => Adapt(e.NewSize.Width);
    }

    /// <summary>
    /// Whether the battery percentage has been dropped for want of room.
    /// </summary>
    internal bool IsShowingBatteryPercentage => BatteryText.Visibility == Visibility.Visible;

    /// <summary>
    /// Drops the least useful reading when the strip is given less room than
    /// it asked for.
    ///
    /// The strip is a row of readouts, and at a larger text size it is simply
    /// wider - wide enough, at 150%, to run off the right of the window and
    /// take the clock with it. Something has to give, and the percentage is
    /// the right thing: the battery glyph already shows the level, and the
    /// exact number stays available to a screen reader through the automation
    /// name, which is set whether or not the text is shown.
    ///
    /// Measured rather than guessed from the window width, because what
    /// matters is whether THIS strip fits, and that depends on the language as
    /// much as on the monitor - "100 %" and "100 %" are not the same width in
    /// every font size.
    /// </summary>
    private void Adapt(double available)
    {
        if (available <= 0 || double.IsNaN(available) || _status is null)
        {
            return;
        }

        // Ask for everything first, or the strip could never recover the
        // percentage once it had been dropped.
        if (_status.BatteryPercent is not null)
        {
            BatteryText.Visibility = Visibility.Visible;
        }

        Strip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        if (Strip.DesiredSize.Width > available)
        {
            BatteryText.Visibility = Visibility.Collapsed;
        }
    }

    public void Initialize(ISystemStatusService status)
    {
        Detach();
        _status = status;
        _status.PropertyChangedSafe(OnStatusChanged);
        Render();
    }

    private void Detach()
    {
        if (_status is INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged -= OnStatusChanged;
        }

        _status = null;
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_status is null)
        {
            return;
        }

        ClockText.Text = _status.Time;
        AutomationProperties.SetName(ClockText, Strings.Format("Status.ClockAutomation", _status.Time));

        // Network state is carried by the glyph AND the accessible name, never
        // by colour alone.
        NetworkGlyph.Glyph = _status.IsOnline ? "" : "";
        AutomationProperties.SetName(
            NetworkGlyph,
            Strings.Get(_status.IsOnline ? "Status.NetworkOnline" : "Status.NetworkOffline"));

        if (_status.HasBattery && _status.BatteryPercent is { } percent)
        {
            BatteryPanel.Visibility = Visibility.Visible;
            BatteryText.Text = string.Format(CultureInfo.CurrentCulture, "{0} %", percent);
            BatteryGlyph.Glyph = BatteryGlyphFor(percent);
            AutomationProperties.SetName(BatteryPanel, Strings.Format("Status.BatteryAutomation", percent));
        }
        else
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Segoe Fluent Icons battery ramp, empty (E850) through full (E83F).</summary>
    private static string BatteryGlyphFor(int percent)
    {
        var step = Math.Clamp(percent / 10, 0, 10);
        return step >= 10 ? "" : ((char)(0xE850 + step)).ToString();
    }
}

internal static class StatusStripExtensions
{
    /// <summary>Subscribes to PropertyChanged when the service supports it.</summary>
    public static void PropertyChangedSafe(this ISystemStatusService status, PropertyChangedEventHandler handler)
    {
        if (status is INotifyPropertyChanged notifier)
        {
            notifier.PropertyChanged += handler;
        }
    }
}
