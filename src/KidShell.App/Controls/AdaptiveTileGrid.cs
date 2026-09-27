using KidShell.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace KidShell.App.Controls;

/// <summary>One line of text a card has to find room for.</summary>
/// <param name="StyleKey">The TextBlock style it is drawn with.</param>
/// <param name="Samples">
/// EVERY string that line is ever drawn with, not a representative one.
///
/// This took a hard-coded "Skogen" once, which is six characters, while the
/// grid also holds "Dinosaurier" at eleven. At 200% the longer label wrapped
/// to a second line the card had not been given room for, and the theme cards
/// hung 20 epx below the panel at every size where the page did not scroll.
/// The worst case is the only one worth measuring, and it is not something a
/// call site should have to work out.
/// </param>
/// <param name="Wraps">
/// Whether the line may break. Only lines that may NOT break widen the card:
/// a hint that wraps would otherwise drag every card out to the width of its
/// longest sentence.
/// </param>
internal sealed record CardText(string StyleKey, IReadOnlyList<string> Samples, bool Wraps = true)
{
    public CardText(string styleKey, string sample, bool wraps = true)
        : this(styleKey, [sample], wraps)
    {
    }
}

/// <summary>
/// The shape a card is drawn at when the text is an ordinary size.
/// </summary>
/// <param name="DesignWidth">The card's drawn width. A floor.</param>
/// <param name="DesignHeight">The card's drawn height. A floor.</param>
/// <param name="SideDecoration">Padding and borders either side of the text.</param>
/// <param name="StackDecoration">Artwork, padding and gaps above and below it.</param>
internal readonly record struct CardShape(
    double DesignWidth,
    double DesignHeight,
    double SideDecoration,
    double StackDecoration);

/// <summary>
/// Grows a grid of cards to whatever their text actually needs.
///
/// WHY THE CARDS CANNOT SIMPLY BE ASKED
/// ------------------------------------
/// UniformGridLayout gives every item the same size and measures each one
/// against exactly that size, so a card always reports back the height it was
/// given. Asking a realized card how tall it wants to be therefore returns
/// MinItemHeight, whatever is inside it - the answer is the question.
///
/// Re-measuring a live card with an unbounded height would answer it, and
/// would also overwrite the DesiredSize its parent is about to arrange from.
/// An earlier version of this file did that and produced tens of thousands of
/// layout defects across the entire app, at every text size, because every
/// container downstream was then arranging from a number that no longer
/// matched what the layout pass had computed.
///
/// So the measurement happens on a TextBlock that is NOT in the visual tree.
/// It renders with the same styles and the same text scaling as the real one -
/// Windows scales text at draw time, not per element - and it belongs to
/// nobody, so measuring it disturbs nothing.
///
/// The design size stays as the floor: it is what the cards look like at
/// ordinary text sizes, and nothing here changes that.
/// </summary>
internal static class AdaptiveTileGrid
{
    /// <summary>
    /// Sets the card size to fit its decoration plus its text.
    /// </summary>
    /// <param name="repeater">The grid whose cards should fit.</param>
    /// <param name="shape">What the card is drawn at normally, and what in it is not text.</param>
    /// <param name="lines">The text the card has to hold.</param>
    public static void Fit(ItemsRepeater repeater, CardShape shape, params CardText[] lines)
    {
        ArgumentNullException.ThrowIfNull(repeater);
        ArgumentNullException.ThrowIfNull(lines);

        if (repeater.Layout is not UniformGridLayout layout)
        {
            return;
        }

        // Width first, because how wide the card ends up decides how many
        // lines the text wraps into and therefore how tall it has to be.
        //
        // Only the lines that cannot break are allowed to widen it. The age
        // card reads "10+", which needed 137 epx at 200% inside a 104-epx
        // card and lost its "+"; splitting that across two lines would not
        // have been an improvement.
        var widest = 0d;

        foreach (var line in lines)
        {
            if (!line.Wraps)
            {
                widest = Math.Max(widest, Measure(line, double.PositiveInfinity).Width);
            }
        }

        var wantedWidth = ResponsiveLayout.CardWidth(shape.DesignWidth, shape.SideDecoration, widest);

        // Measured against the width the card is about to have, not the one it
        // has now, or the first pass at a new text size would size the height
        // for a card that no longer exists.
        var textWidth = ResponsiveLayout.CardTextWidth(wantedWidth, shape.SideDecoration);
        var textHeight = 0d;

        foreach (var line in lines)
        {
            textHeight += Measure(line, textWidth).Height;
        }

        var wantedHeight = ResponsiveLayout.CardHeight(shape.DesignHeight, shape.StackDecoration, textHeight);

        // Only when they actually changed: assigning these invalidates the
        // layout, and doing so on every pass would never settle.
        if (Math.Abs(layout.MinItemWidth - wantedWidth) > 0.5)
        {
            layout.MinItemWidth = wantedWidth;
        }

        if (Math.Abs(layout.MinItemHeight - wantedHeight) > 0.5)
        {
            layout.MinItemHeight = wantedHeight;
        }
    }

    /// <summary>
    /// The biggest this line gets, over every string it is ever drawn with.
    /// </summary>
    private static Size Measure(CardText line, double width)
    {
        var widest = 0d;
        var tallest = 0d;

        foreach (var sample in line.Samples)
        {
            var probe = new TextBlock
            {
                Text = sample,
                TextWrapping = line.Wraps ? TextWrapping.Wrap : TextWrapping.NoWrap
            };

            if (Application.Current?.Resources.TryGetValue(line.StyleKey, out var style) is true &&
                style is Style textStyle)
            {
                probe.Style = textStyle;
            }

            probe.Measure(new Size(width, double.PositiveInfinity));

            widest = Math.Max(widest, probe.DesiredSize.Width);
            tallest = Math.Max(tallest, probe.DesiredSize.Height);
        }

        return new Size(widest, tallest);
    }
}
