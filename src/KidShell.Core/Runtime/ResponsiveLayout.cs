namespace KidShell.Core.Runtime;

/// <summary>
/// How much room there is, in effective pixels.
///
/// Effective pixels, not physical: Windows text scaling at 150% turns a
/// 1920-pixel monitor into 1280 effective pixels, and a layout that reasons
/// about physical pixels breaks for exactly the people who need scaling.
/// </summary>
public enum LayoutSize
{
    /// <summary>
    /// Under 900 epx. A 1366x768 window at 150%, a narrow resized window, or a
    /// small tablet. Everything that can stack, stacks.
    /// </summary>
    Compact = 0,

    /// <summary>900-1279 epx. 1024x768, or 1920x1080 at 175%.</summary>
    Medium = 1,

    /// <summary>1280-1919 epx. 1366x768 and 1920x1080 at 150%.</summary>
    Wide = 2,

    /// <summary>1920 epx and up. 1920x1080 at 100%, 2560x1440 at 125%.</summary>
    ExtraWide = 3
}

/// <summary>How Parent Mode's navigation is drawn.</summary>
public enum NavigationMode
{
    /// <summary>Icons only, no labels. Buys back roughly 130 epx.</summary>
    Rail = 0,

    /// <summary>Icons and labels, the normal appearance.</summary>
    Expanded = 1
}

/// <summary>
/// The layout decisions, kept out of XAML so they can be tested.
///
/// WHY THIS IS CODE RATHER THAN VisualStateManager TRIGGERS
/// --------------------------------------------------------
/// Adaptive triggers are declarative and untestable: the only way to know what
/// a layout does at 1024x768 with 150% scaling is to build it and look. Every
/// responsiveness bug in this codebase so far has been of exactly that shape -
/// a MaxHeight that looked fine on the machine it was written on and hid two
/// radio buttons everywhere else.
///
/// So the rules live here, in ordinary code with ordinary tests, and the views
/// ask rather than decide. A breakpoint that is wrong is then a failing test
/// rather than something somebody has to notice.
/// </summary>
public static class ResponsiveLayout
{
    /// <summary>Below this, panels stack and navigation collapses to a rail.</summary>
    public const double CompactThreshold = 900;

    public const double MediumThreshold = 1280;

    public const double WideThreshold = 1920;

    /// <summary>Width of the navigation when it shows labels.</summary>
    public const double ExpandedNavigationWidth = 218;

    /// <summary>
    /// Width of the navigation when it shows icons only.
    ///
    /// 72 rather than something tighter: the panel spends 24 on its own
    /// padding, leaving 48 for the button - which is about the smallest
    /// comfortable touch target, and exactly enough to centre a 26-wide glyph.
    /// </summary>
    public const double RailNavigationWidth = 72;

    /// <summary>Width of the summary column beside the content.</summary>
    public const double AsideWidth = 320;

    /// <summary>
    /// The narrowest the content column may become before something else has
    /// to give. Below this a settings form stops being readable: a label, an
    /// input and a description no longer fit on sensible lines.
    /// </summary>
    public const double MinimumContentWidth = 420;

    public static LayoutSize Classify(double width) => width switch
    {
        < CompactThreshold => LayoutSize.Compact,
        < MediumThreshold => LayoutSize.Medium,
        < WideThreshold => LayoutSize.Wide,
        _ => LayoutSize.ExtraWide
    };

    /// <summary>
    /// Whether the navigation shows labels.
    ///
    /// A rail on a compact window is not a style choice: at 1024 epx the
    /// expanded sidebar plus the aside column leave roughly 400 epx of content,
    /// which is narrower than a single settings row needs.
    /// </summary>
    public static NavigationMode DecideNavigation(double width) =>
        Classify(width) == LayoutSize.Compact ? NavigationMode.Rail : NavigationMode.Expanded;

    /// <summary>
    /// Whether the summary column beside the content is shown.
    ///
    /// It is a convenience, not a necessity - everything in it is reachable
    /// from a page. So it is the first thing dropped, and it is dropped before
    /// the content column gets squeezed rather than after.
    /// </summary>
    public static bool ShowAside(double width)
    {
        if (Classify(width) == LayoutSize.Compact)
        {
            return false;
        }

        var navigation = DecideNavigation(width) == NavigationMode.Expanded
            ? ExpandedNavigationWidth
            : RailNavigationWidth;

        // Chrome: page padding, the two column gaps, and the panel padding.
        const double chrome = 96;

        return width - navigation - AsideWidth - chrome >= MinimumContentWidth;
    }

    /// <summary>
    /// How many columns of cards fit.
    ///
    /// Derived from the space actually available rather than from a breakpoint
    /// table, because the two disagree: the same window is wide for a child's
    /// app grid and narrow for one holding a sidebar and an aside column.
    ///
    /// Never returns more columns than there are items, so four apps do not
    /// become four lonely cards on one row of an eight-wide grid, and never
    /// fewer than one.
    /// </summary>
    public static int ColumnsFor(double availableWidth, double minItemWidth, int itemCount, int maxColumns = 4)
    {
        if (itemCount <= 0 || minItemWidth <= 0 || maxColumns <= 0)
        {
            return 1;
        }

        var fits = (int)Math.Floor(availableWidth / minItemWidth);

        // At least one column even when the window is narrower than a card -
        // a clipped card the child can scroll to beats no card at all.
        return Math.Clamp(Math.Min(fits, maxColumns), 1, Math.Min(itemCount, maxColumns));
    }

    /// <summary>
    /// How tall a scrollable region inside a dialog or a card may be.
    ///
    /// A fixed MaxHeight is the single most common responsiveness bug in this
    /// codebase: 300 epx looked generous on a 1440-tall monitor and hid two
    /// radio buttons at 768 with scaling, with no visible scrollbar to hint
    /// that anything was missing.
    ///
    /// This gives the region a share of what is actually there, floored so it
    /// stays usable on a very short window and capped so it does not become a
    /// single enormous scroll on a large one.
    /// </summary>
    public static double ScrollableHeight(
        double availableHeight,
        double reservedForChrome,
        double minimum = 180,
        double maximum = 560)
    {
        var usable = availableHeight - reservedForChrome;

        if (double.IsNaN(usable) || double.IsInfinity(usable))
        {
            return minimum;
        }

        return Math.Clamp(usable, minimum, maximum);
    }

    /// <summary>
    /// How wide a dialog may be.
    ///
    /// Capped so a line of text stays readable on a wide monitor, and bounded
    /// by the window so it cannot extend past the usable display on a small
    /// one - which is what a fixed Width does at 200% scaling.
    /// </summary>
    public static double DialogWidth(double availableWidth, double preferred = 520, double minimum = 300)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return minimum;
        }

        // Leave room for the dialog's own padding and the dimmed margin around
        // it; a dialog flush against both edges reads as a broken window.
        var bounded = availableWidth - 96;

        return Math.Clamp(Math.Min(preferred, bounded), minimum, preferred);
    }

    /// <summary>
    /// Whether panels that sit side by side should stack instead.
    ///
    /// Two columns are worth having only while each is wide enough to read. A
    /// pair of 200-epx columns is worse than one 420-epx column, so the answer
    /// is about the resulting width rather than about the window.
    /// </summary>
    public static bool ShouldStack(double availableWidth, int panels, double minimumPanelWidth = 320)
    {
        if (panels <= 1)
        {
            return false;
        }

        const double gap = 18;
        var needed = (minimumPanelWidth * panels) + (gap * (panels - 1));

        return availableWidth < needed;
    }

    /// <summary>
    /// The height below which a tall fixed panel has to give up its
    /// decoration.
    ///
    /// Several supported configurations are short rather than narrow:
    /// 1440x900 and 1600x900 at 150% both give 600 effective pixels of
    /// height, and 1920x1080 at 175% gives 617. A panel that is centred
    /// vertically and taller than that does not merely look cramped - it hangs
    /// off both ends, and whatever is at the bottom becomes unreachable.
    /// </summary>
    public const double ShortWindowHeight = 700;

    /// <summary>
    /// Whether the window is too short to afford decorative height.
    ///
    /// Decoration means an illustration or a large icon that carries no
    /// information. Text is never the thing that gives way: the rule is to
    /// drop ornament first, then scroll, and never to shrink the words.
    /// </summary>
    public static bool IsShort(double height) =>
        !double.IsNaN(height) && height > 0 && height < ShortWindowHeight;

    /// <summary>
    /// The narrowest window KidShell is designed for.
    ///
    /// Everything narrower is still laid out without losing anything, because
    /// the window's own minimum is set through an API whose unit is
    /// undocumented and which has an open bug about moving between displays of
    /// different scale. Below this, decoration is dropped rather than
    /// squeezed - the child's avatar at 640 epx was a vertical sliver.
    /// </summary>
    public const double MinimumSupportedWidth = 780;

    /// <summary>The height a Barnläge app card is drawn at when there is room.</summary>
    public const double PreferredTileHeight = 186;

    /// <summary>
    /// The smallest a card may get before it stops being a card.
    ///
    /// The icon and the name both have to stay legible from a distance, by a
    /// child who may not read well. Below this the honest answer is to scroll,
    /// not to keep shrinking.
    /// </summary>
    public const double MinimumTileHeight = 138;

    /// <summary>
    /// How tall each app card should be so that a given number of rows fits.
    ///
    /// On a short window the cards kept their full height, two rows came to
    /// more than the space available, and because the grid is centred the
    /// result was a row sliced through the middle of its label at the bottom
    /// and nothing to suggest it. Trimming the card a little is much better
    /// than that, and much better than shrinking the name inside it - so the
    /// card gives way down to a floor, and past the floor the grid scrolls.
    /// </summary>
    public static double TileHeight(
        double availableHeight,
        int rows = 2,
        double spacing = 22,
        double preferred = PreferredTileHeight,
        double minimum = MinimumTileHeight)
    {
        if (double.IsNaN(availableHeight) || availableHeight <= 0 || rows <= 0)
        {
            return preferred;
        }

        var perRow = (availableHeight - (spacing * (rows - 1))) / rows;

        return Math.Clamp(perRow, minimum, preferred);
    }
}
