using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Controls;

/// <summary>
/// Lays children out in a row, wrapping to the next line when they no longer
/// fit.
///
/// WinUI has no WrapPanel, and KidShell needs one in exactly the places where a
/// horizontal StackPanel becomes a bug: a row of buttons at 1366x768 with
/// Windows scaling at 150%, or a row of Swedish labels that turn out longer
/// than the English ones somebody sized the layout against. Both clip silently,
/// which is the worst way for a layout to fail - it looks fine on the machine
/// it was built on.
///
/// Deliberately small. This is horizontal wrapping and nothing else; it is not
/// the start of a layout library.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0d, OnLayoutPropertyChanged));

    /// <summary>Gap between items on the same row.</summary>
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    /// <summary>Gap between rows.</summary>
    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        (d as WrapPanel)?.InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        // An unbounded width means "measure as one row" - a ScrollViewer asking
        // how wide this would like to be. Wrapping against infinity would put
        // everything on one line and then clip it.
        var limit = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        double rowWidth = 0, rowHeight = 0, totalWidth = 0, totalHeight = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(limit, availableSize.Height));
            var size = child.DesiredSize;

            var needed = rowWidth == 0 ? size.Width : rowWidth + HorizontalSpacing + size.Width;

            if (needed > limit && rowWidth > 0)
            {
                totalWidth = Math.Max(totalWidth, rowWidth);
                totalHeight += rowHeight + VerticalSpacing;
                rowWidth = size.Width;
                rowHeight = size.Height;
                continue;
            }

            rowWidth = needed;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        totalWidth = Math.Max(totalWidth, rowWidth);
        totalHeight += rowHeight;

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            if (x > 0 && x + size.Width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));

            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }
}
