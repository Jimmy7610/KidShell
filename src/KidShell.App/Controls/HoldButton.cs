using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace KidShell.App.Controls;

/// <summary>
/// A button that only fires after being held down for a while.
///
/// This is the adult door in Child Mode: a six-year-old brushing the corner of
/// the screen should not land in Parent Mode, but a parent holding the button
/// for three seconds should. Keyboard activation runs the same timer, so the
/// gesture is reachable without a pointer.
/// </summary>
public sealed partial class HoldButton : Button
{
    private const double TickMilliseconds = 25;

    private readonly DispatcherTimer _timer;
    private DateTimeOffset _startedAt;
    private bool _completed;

    public HoldButton()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMilliseconds) };
        _timer.Tick += OnTick;

        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressedHandler), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedHandler), handledEventsToo: true);
        PointerExited += (_, _) => CancelHold();
        PointerCanceled += (_, _) => CancelHold();
        PointerCaptureLost += (_, _) => CancelHold();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += (_, _) => CancelHold();
        Unloaded += (_, _) => _timer.Stop();
    }

    public static readonly DependencyProperty HoldSecondsProperty = DependencyProperty.Register(
        nameof(HoldSeconds),
        typeof(double),
        typeof(HoldButton),
        new PropertyMetadata(3.0));

    /// <summary>How long the button must be held, in seconds.</summary>
    public double HoldSeconds
    {
        get => (double)GetValue(HoldSecondsProperty);
        set => SetValue(HoldSecondsProperty, value);
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress),
        typeof(double),
        typeof(HoldButton),
        new PropertyMetadata(0.0));

    /// <summary>Hold progress from 0 to 100, ready to drive a ProgressRing.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        private set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty IsHoldingProperty = DependencyProperty.Register(
        nameof(IsHolding),
        typeof(bool),
        typeof(HoldButton),
        new PropertyMetadata(false));

    public bool IsHolding
    {
        get => (bool)GetValue(IsHoldingProperty);
        private set => SetValue(IsHoldingProperty, value);
    }

    /// <summary>Raised once the button has been held for <see cref="HoldSeconds"/>.</summary>
    public event EventHandler? Held;

    private void OnPointerPressedHandler(object sender, PointerRoutedEventArgs e) => BeginHold();

    private void OnPointerReleasedHandler(object sender, PointerRoutedEventArgs e) => CancelHold();

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Space or VirtualKey.Enter)
        {
            BeginHold();
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Space or VirtualKey.Enter)
        {
            CancelHold();
        }
    }

    private void BeginHold()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        _completed = false;
        _startedAt = DateTimeOffset.UtcNow;
        Progress = 0;
        IsHolding = true;
        _timer.Start();
    }

    private void CancelHold()
    {
        _timer.Stop();
        IsHolding = false;
        Progress = 0;
    }

    private void OnTick(object? sender, object e)
    {
        var seconds = Math.Max(HoldSeconds, 0.2);
        var elapsed = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
        Progress = Math.Clamp(elapsed / seconds * 100.0, 0, 100);

        if (elapsed < seconds || _completed)
        {
            return;
        }

        _completed = true;
        CancelHold();
        Held?.Invoke(this, EventArgs.Empty);
    }
}
