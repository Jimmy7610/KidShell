using System.ComponentModel;
using KidShell.App.Localization;
using KidShell.App.ViewModels;
using KidShell.Core.Runtime;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace KidShell.App.Views;

/// <summary>The parent PIN dialog.</summary>
public sealed partial class PinOverlayView : UserControl
{
    private PinOverlayViewModel? _viewModel;
    private readonly List<Ellipse> _dots = [];

    public PinOverlayView()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Height);
    }

    /// <summary>
    /// Trims the card's ornament on a short window.
    ///
    /// The keypad scrolls if it has to, but scrolling to reach Cancel is a
    /// poor experience for the one screen a parent uses most, so the height
    /// that can be given back without losing anything is given back first:
    /// the padlock badge is decoration, and the generous padding around the
    /// card is there to look calm rather than to say anything.
    /// </summary>
    private void ApplyLayout(double height)
    {
        var tight = ResponsiveLayout.IsShort(height);

        LockBadge.Visibility = tight ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Margin = tight ? new Thickness(0) : new Thickness(0, 12, 0, 0);
        Dots.Margin = tight ? new Thickness(0, 10, 0, 2) : new Thickness(0, 18, 0, 4);
        Keypad.Margin = tight ? new Thickness(0, 8, 0, 0) : new Thickness(0, 16, 0, 0);
        CancelButton.Margin = tight ? new Thickness(0, 10, 0, 0) : new Thickness(0, 18, 0, 0);
        Card.Padding = tight ? new Thickness(34, 18, 34, 16) : new Thickness(34, 30, 34, 26);
    }

    public void Initialize(PinOverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        BuildDots(viewModel.PinLength);
        Render();
    }

    /// <summary>Called whenever the overlay becomes visible.</summary>
    public void PrepareForEntry()
    {
        Render();
        CancelButton.Focus(FocusState.Programmatic);
    }

    private void BuildDots(int count)
    {
        Dots.Children.Clear();
        _dots.Clear();

        for (var i = 0; i < count; i++)
        {
            var dot = new Ellipse
            {
                Width = 16,
                Height = 16,
                StrokeThickness = 2,
                Stroke = (Brush)Application.Current.Resources["StatusInactiveBrush"],
                Fill = new SolidColorBrush(Colors.Transparent)
            };

            _dots.Add(dot);
            Dots.Children.Add(dot);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        var filled = (Brush)Application.Current.Resources["BrandBlueLightBrush"];
        var empty = new SolidColorBrush(Colors.Transparent);
        var outlineIdle = (Brush)Application.Current.Resources["StatusInactiveBrush"];
        var outlineError = (Brush)Application.Current.Resources["StatusAlertBrush"];

        for (var i = 0; i < _dots.Count; i++)
        {
            var isFilled = i < _viewModel.EnteredCount;
            _dots[i].Fill = isFilled ? filled : empty;
            _dots[i].Stroke = _viewModel.HasError ? outlineError : outlineIdle;
        }

        ErrorPanel.Visibility = _viewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
        DeveloperHint.Visibility = _viewModel.ShowsDeveloperHint ? Visibility.Visible : Visibility.Collapsed;

        AutomationProperties.SetName(Dots, _viewModel.AutomationStatus);

        if (_viewModel.HasError)
        {
            // Announce the failure rather than relying on the red outline.
            AutomationProperties.SetName(ErrorPanel, Strings.Get("Pin.Wrong"));
        }
    }

    private void OnDigitClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string digit })
        {
            _viewModel?.Append(digit);
        }
    }

    private void OnBackspaceClick(object sender, RoutedEventArgs e) => _viewModel?.Backspace();

    private void OnClearClick(object sender, RoutedEventArgs e) => _viewModel?.Clear();

    private void OnCancelClick(object sender, RoutedEventArgs e) => _viewModel?.CancelCommand.Execute(null);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case >= VirtualKey.Number0 and <= VirtualKey.Number9:
                _viewModel.Append(((int)e.Key - (int)VirtualKey.Number0).ToString());
                e.Handled = true;
                break;

            case >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9:
                _viewModel.Append(((int)e.Key - (int)VirtualKey.NumberPad0).ToString());
                e.Handled = true;
                break;

            case VirtualKey.Back:
                _viewModel.Backspace();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                _viewModel.CancelCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
