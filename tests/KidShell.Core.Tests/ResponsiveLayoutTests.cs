using KidShell.Core.Runtime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The layout decisions, tested at the sizes KidShell actually has to work on.
///
/// The widths below are effective pixels, which is what a layout sees. A
/// 1920-pixel monitor at 150% scaling is 1280 epx, and a rule written against
/// physical pixels breaks for exactly the people who turn scaling up.
/// </summary>
public class ResponsiveLayoutTests
{
    /// <summary>
    /// The supported matrix, converted to effective pixels.
    ///
    /// Each row is a real combination somebody runs: the resolution, the
    /// Windows scale factor, and what the layout therefore has to work with.
    /// </summary>
    public static TheoryData<int, int, double> SupportedSizes() => new()
    {
        // physical width, scale %, effective width
        { 1024, 100, 1024 },
        { 1280, 100, 1280 },
        { 1366, 100, 1366 },
        { 1440, 100, 1440 },
        { 1600, 100, 1600 },
        { 1920, 100, 1920 },
        { 2560, 100, 2560 },
        { 3840, 100, 3840 },

        { 1366, 125, 1092.8 },
        { 1920, 125, 1536 },
        { 2560, 125, 2048 },

        { 1366, 150, 910.7 },
        { 1920, 150, 1280 },
        { 2560, 150, 1706.7 },
        { 3840, 150, 2560 },

        { 1920, 175, 1097.1 },
        { 2560, 175, 1462.9 },

        { 1920, 200, 960 },
        { 2560, 200, 1280 },
        { 3840, 200, 1920 }
    };

    // ------------------------------------------------------ classification

    [Theory]
    [InlineData(800, LayoutSize.Compact)]
    [InlineData(899, LayoutSize.Compact)]
    [InlineData(900, LayoutSize.Medium)]
    [InlineData(1024, LayoutSize.Medium)]
    [InlineData(1279, LayoutSize.Medium)]
    [InlineData(1280, LayoutSize.Wide)]
    [InlineData(1366, LayoutSize.Wide)]
    [InlineData(1919, LayoutSize.Wide)]
    [InlineData(1920, LayoutSize.ExtraWide)]
    [InlineData(3840, LayoutSize.ExtraWide)]
    public void Widths_classify_at_the_documented_boundaries(double width, LayoutSize expected) =>
        Assert.Equal(expected, ResponsiveLayout.Classify(width));

    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void Every_supported_size_leaves_a_readable_content_column(
        int physical, int scale, double effective)
    {
        // THE RULE THAT MATTERS. Whatever the navigation and aside decide to
        // do, the content column must stay wide enough to read a settings row.
        var navigation = ResponsiveLayout.DecideNavigation(effective) == NavigationMode.Expanded
            ? ResponsiveLayout.ExpandedNavigationWidth
            : ResponsiveLayout.RailNavigationWidth;

        var aside = ResponsiveLayout.ShowAside(effective) ? ResponsiveLayout.AsideWidth : 0;
        const double chrome = 96;

        var content = effective - navigation - aside - chrome;

        Assert.True(content >= ResponsiveLayout.MinimumContentWidth,
            $"{physical}px @ {scale}% ({effective:F0} epx) leaves only {content:F0} epx of content");
    }

    // ---------------------------------------------------------- navigation

    [Fact]
    public void The_navigation_collapses_to_a_rail_when_space_is_tight()
    {
        // 1366x768 at 150% is 911 epx - just over compact, but the aside has
        // to go. At 1024 at 125% (819 epx) the labels go too.
        Assert.Equal(NavigationMode.Rail, ResponsiveLayout.DecideNavigation(819));
        Assert.Equal(NavigationMode.Expanded, ResponsiveLayout.DecideNavigation(911));
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(1366)]
    [InlineData(1280)]
    public void The_navigation_keeps_its_labels_on_ordinary_displays(double width) =>
        Assert.Equal(NavigationMode.Expanded, ResponsiveLayout.DecideNavigation(width));

    [Fact]
    public void The_aside_column_is_dropped_before_the_content_is_squeezed()
    {
        // The aside is a convenience; everything in it is reachable from a
        // page. So it goes first, and it goes while the content is still
        // comfortable rather than after it has become unusable.
        Assert.False(ResponsiveLayout.ShowAside(1024));
        Assert.True(ResponsiveLayout.ShowAside(1366));
    }

    [Fact]
    public void A_compact_window_never_shows_the_aside()
    {
        Assert.False(ResponsiveLayout.ShowAside(880));
        Assert.False(ResponsiveLayout.ShowAside(700));
    }

    // ------------------------------------------------------------- columns

    [Theory]
    [InlineData(1180, 8, 4)]
    [InlineData(900, 8, 3)]
    [InlineData(700, 8, 3)]
    [InlineData(500, 8, 2)]
    [InlineData(250, 8, 1)]
    public void The_child_grid_drops_columns_rather_than_shrinking_cards(
        double width, int apps, int expected)
    {
        Assert.Equal(expected, ResponsiveLayout.ColumnsFor(width, minItemWidth: 232, itemCount: apps));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(8, 4)]
    [InlineData(20, 4)]
    public void The_grid_never_has_more_columns_than_apps(int apps, int expected)
    {
        // Four apps on an eight-wide grid would be four lonely cards on one
        // row with a large empty space beside them.
        Assert.Equal(expected, ResponsiveLayout.ColumnsFor(2000, minItemWidth: 232, itemCount: apps));
    }

    [Fact]
    public void A_window_narrower_than_one_card_still_shows_one()
    {
        // A card the child can scroll to beats no card at all.
        Assert.Equal(1, ResponsiveLayout.ColumnsFor(120, minItemWidth: 232, itemCount: 8));
    }

    [Fact]
    public void Nonsense_input_yields_one_column_rather_than_throwing()
    {
        Assert.Equal(1, ResponsiveLayout.ColumnsFor(1000, minItemWidth: 0, itemCount: 8));
        Assert.Equal(1, ResponsiveLayout.ColumnsFor(1000, minItemWidth: 232, itemCount: 0));
        Assert.Equal(1, ResponsiveLayout.ColumnsFor(-50, minItemWidth: 232, itemCount: 8));
    }

    // ------------------------------------------------------- scroll height

    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void A_scrollable_region_always_gets_a_usable_height(
        int physical, int scale, double effectiveWidth)
    {
        // 4:3 is the shortest supported shape, so derive a pessimistic height.
        var effectiveHeight = effectiveWidth * 0.75;

        var height = ResponsiveLayout.ScrollableHeight(effectiveHeight, reservedForChrome: 320);

        Assert.True(height >= 180, $"{physical}px @ {scale}% gave only {height:F0} epx");
        Assert.True(height <= 560);
    }

    [Fact]
    public void A_short_window_still_gets_the_minimum_rather_than_a_negative_height()
    {
        // 768 tall at 200% is 384 epx. After chrome there is nothing left, and
        // the answer must be a usable minimum with a scrollbar, not zero.
        Assert.Equal(180, ResponsiveLayout.ScrollableHeight(384, reservedForChrome: 320));
    }

    [Fact]
    public void A_tall_window_does_not_become_one_enormous_scroll()
    {
        Assert.Equal(560, ResponsiveLayout.ScrollableHeight(2160, reservedForChrome: 320));
    }

    [Fact]
    public void A_nonsense_height_falls_back_to_the_minimum()
    {
        Assert.Equal(180, ResponsiveLayout.ScrollableHeight(double.NaN, 320));
        Assert.Equal(180, ResponsiveLayout.ScrollableHeight(double.PositiveInfinity, 320));
    }

    // -------------------------------------------------------- dialog width

    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void A_dialog_never_extends_past_the_window(int physical, int scale, double effective)
    {
        var width = ResponsiveLayout.DialogWidth(effective);

        Assert.True(width <= effective,
            $"{physical}px @ {scale}%: dialog {width:F0} epx in a {effective:F0} epx window");

        Assert.True(width >= 300);
    }

    [Fact]
    public void A_dialog_stops_growing_on_a_wide_monitor()
    {
        // A 3000-epx line of text is not readable, however much room there is.
        Assert.Equal(520, ResponsiveLayout.DialogWidth(3840));
    }

    [Fact]
    public void A_dialog_shrinks_on_a_narrow_window()
    {
        // 1920 at 200% is 960 epx; at 1024 at 200% it is 512, and a 520-wide
        // dialog would sit flush against both edges.
        Assert.True(ResponsiveLayout.DialogWidth(512) < 520);
    }

    // ------------------------------------------------------------ stacking

    [Fact]
    public void Two_panels_stack_rather_than_becoming_two_narrow_columns()
    {
        // A pair of 200-epx columns is worse than one 420-epx column.
        Assert.True(ResponsiveLayout.ShouldStack(600, panels: 2));
        Assert.False(ResponsiveLayout.ShouldStack(900, panels: 2));
    }

    [Fact]
    public void A_single_panel_never_stacks() =>
        Assert.False(ResponsiveLayout.ShouldStack(200, panels: 1));

    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void The_overview_cards_stack_only_when_they_have_to(
        int physical, int scale, double effective)
    {
        var navigation = ResponsiveLayout.DecideNavigation(effective) == NavigationMode.Expanded
            ? ResponsiveLayout.ExpandedNavigationWidth
            : ResponsiveLayout.RailNavigationWidth;

        var aside = ResponsiveLayout.ShowAside(effective) ? ResponsiveLayout.AsideWidth : 0;
        var content = effective - navigation - aside - 96;

        var stacked = ResponsiveLayout.ShouldStack(content, panels: 2);

        // Whichever it chose, the result must be readable: either two columns
        // of at least 320, or one column of at least the minimum.
        if (stacked)
        {
            Assert.True(content >= ResponsiveLayout.MinimumContentWidth);
        }
        else
        {
            Assert.True((content - 18) / 2 >= 320,
                $"{physical}px @ {scale}% kept two columns of {(content - 18) / 2:F0} epx");
        }
    }

    [Theory]
    [InlineData(560, true)]    // the smallest window KidShell supports
    [InlineData(600, true)]    // 1440x900 and 1600x900 at 150%
    [InlineData(617, true)]    // 1920x1080 at 175%
    [InlineData(699, true)]
    [InlineData(700, false)]
    [InlineData(768, false)]   // 1366x768, the common laptop
    [InlineData(1080, false)]
    public void A_short_window_is_recognised_as_short(double height, bool expected) =>
        Assert.Equal(expected, ResponsiveLayout.IsShort(height));

    [Fact]
    public void A_window_with_no_measured_height_is_not_called_short()
    {
        // Before the first layout pass the height is zero or NaN, and treating
        // that as "short" would strip the decoration off every window for the
        // one frame before the real size arrives.
        Assert.False(ResponsiveLayout.IsShort(0));
        Assert.False(ResponsiveLayout.IsShort(double.NaN));
    }

    [Fact]
    public void An_app_card_keeps_its_full_height_when_there_is_room() =>
        Assert.Equal(
            ResponsiveLayout.PreferredTileHeight,
            ResponsiveLayout.TileHeight(availableHeight: 900));

    [Fact]
    public void An_app_card_gives_way_before_the_grid_has_to_scroll()
    {
        // Two rows in 320 epx: without this the second row was sliced through
        // its own label at 819x614.
        var height = ResponsiveLayout.TileHeight(availableHeight: 320);

        Assert.True(height < ResponsiveLayout.PreferredTileHeight);
        Assert.True((height * 2) + 22 <= 320 + 0.001,
            $"two cards of {height:F0} plus the gap do not fit in 320");
    }

    [Fact]
    public void An_app_card_stops_shrinking_where_a_child_can_still_read_it()
    {
        // Past the floor the answer is to scroll, not to keep shrinking: the
        // icon and the name have to stay legible from across a room.
        Assert.Equal(
            ResponsiveLayout.MinimumTileHeight,
            ResponsiveLayout.TileHeight(availableHeight: 60));

        Assert.Equal(
            ResponsiveLayout.MinimumTileHeight,
            ResponsiveLayout.TileHeight(availableHeight: 1));
    }

    [Fact]
    public void An_app_card_has_a_height_before_anything_has_been_measured() =>
        Assert.Equal(
            ResponsiveLayout.PreferredTileHeight,
            ResponsiveLayout.TileHeight(availableHeight: double.NaN));

    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void Two_rows_of_cards_fit_every_supported_display(
        int physical, int scale, double effective)
    {
        // The grid area is what is left after the greeting and the footer.
        const double chrome = 240;

        var available = EffectiveHeightFor(effective) - chrome;
        var card = ResponsiveLayout.TileHeight(available);

        // Either two rows fit, or the cards are already at the floor and the
        // grid scrolls - what must never happen is a row half drawn.
        var fits = (card * 2) + 22 <= available + 0.001;

        Assert.True(fits || card <= ResponsiveLayout.MinimumTileHeight + 0.001,
            $"{physical}px @ {scale}% got {card:F0}-epx cards in {available:F0} epx");
    }

    // ------------------------------------------------------------------
    // Text scaling: the decisions the chrome makes when the words grow.
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SupportedSizes))]
    public void Decoration_never_takes_more_than_its_share_of_a_row(
        int physical, int scale, double effective)
    {
        var brand = ResponsiveLayout.ShareOfRow(effective, ResponsiveLayout.BrandShare);
        var status = ResponsiveLayout.ShareOfRow(effective, ResponsiveLayout.StatusShare);

        // The point of the cap: whatever the window, the two decorative blocks
        // together leave the majority of the row to the things that do
        // something. A wordmark that wraps is fine; one that pushes the exit
        // buttons off the window is not.
        Assert.True(brand + status < effective * 0.7,
            $"{physical}px @ {scale}%: decoration wanted {brand + status:F0} of {effective:F0} epx");

        Assert.True(double.IsFinite(brand),
            "a cap of infinity is what stopped the header wrapping in the first place");
    }

    [Fact]
    public void A_row_with_no_width_yet_caps_nothing()
    {
        // Before the first layout pass the width is NaN or zero. Returning a
        // real number there would pin MaxWidth to it and never let go.
        Assert.True(double.IsPositiveInfinity(ResponsiveLayout.ShareOfRow(double.NaN, 0.35)));
        Assert.True(double.IsPositiveInfinity(ResponsiveLayout.ShareOfRow(0, 0.35)));
    }

    [Theory]
    [InlineData(640, false)]
    [InlineData(779, false)]
    [InlineData(780, true)]
    [InlineData(1366, true)]
    public void Header_decoration_goes_below_the_narrowest_supported_window(
        double width, bool shown) =>
        Assert.Equal(shown, ResponsiveLayout.ShowHeaderDecoration(width));

    [Theory]
    [InlineData(640, false)]
    [InlineData(899, false)]
    [InlineData(900, true)]
    [InlineData(1920, true)]
    public void The_footer_wordmark_gives_the_row_to_the_buttons_when_compact(
        double width, bool shown) =>
        Assert.Equal(shown, ResponsiveLayout.ShowFooterBrand(width));

    [Fact]
    public void Decoration_is_dropped_before_the_navigation_loses_its_labels()
    {
        // The order matters and is easy to get backwards. Anything that is
        // still showing its wordmark must also still be showing its labels.
        foreach (var width in new double[] { 640, 700, 780, 800, 819, 960, 1024, 1280, 1920, 3840 })
        {
            if (ResponsiveLayout.ShowFooterBrand(width))
            {
                Assert.Equal(NavigationMode.Expanded, ResponsiveLayout.DecideNavigation(width));
            }
        }
    }

    [Fact]
    public void A_card_grows_for_text_that_needs_it_and_never_shrinks_below_the_design()
    {
        const double design = 198;
        const double decoration = 92;

        // Ordinary text sizes: the card looks exactly as it was drawn.
        Assert.Equal(design, ResponsiveLayout.CardHeight(design, decoration, 60));

        // Text that has outgrown the card: the card grows rather than the
        // words shrinking or the label being sliced through.
        Assert.Equal(decoration + 140, ResponsiveLayout.CardHeight(design, decoration, 140));
    }

    [Fact]
    public void A_card_that_has_not_been_measured_keeps_its_design_height()
    {
        // A probe that has not laid out yet reports NaN. Treating that as a
        // height collapses every card in the grid at once.
        Assert.Equal(198, ResponsiveLayout.CardHeight(198, 92, double.NaN));
        Assert.Equal(198, ResponsiveLayout.CardHeight(198, 92, double.PositiveInfinity));
    }

    [Fact]
    public void Dialog_content_stays_inside_the_dialogs_own_padding()
    {
        // A ContentDialog caps itself at ContentDialogMaxWidth and its padding
        // is INSIDE that cap, so content sized to the cap overflows by exactly
        // the padding. This was hard-coded as "548 less 48" while the padding
        // is 26 either side, and the note at the bottom of the browse dialog
        // was clipped by the 4 epx of difference at every window size.
        Assert.Equal(496, ResponsiveLayout.DialogContentWidth(1366, 548, 52));

        // On a window too narrow for the cap, the window wins.
        Assert.Equal(496, ResponsiveLayout.DialogContentWidth(640, 548, 52));
        Assert.True(ResponsiveLayout.DialogContentWidth(360, 548, 52) <= 360);

        // A theme that gives no usable room falls back rather than returning
        // a negative width.
        Assert.True(ResponsiveLayout.DialogContentWidth(1366, 40, 52) > 0);
    }

    [Fact]
    public void A_card_widens_for_a_line_that_cannot_wrap()
    {
        const double design = 104;

        // Ordinary text sizes: the card stays the square it was drawn as.
        Assert.Equal(design, ResponsiveLayout.CardWidth(design, 0, 60));

        // "10+" at 200% needs 137 and has nowhere to break, so the card grows
        // and the grid fits fewer per row. The alternative was losing the "+".
        Assert.Equal(137, ResponsiveLayout.CardWidth(design, 0, 137));

        // Side padding counts against the text, not for it.
        Assert.Equal(161, ResponsiveLayout.CardWidth(design, 24, 137));
    }

    [Fact]
    public void A_card_that_has_not_been_measured_keeps_its_design_width()
    {
        Assert.Equal(104, ResponsiveLayout.CardWidth(104, 0, double.NaN));
        Assert.Equal(104, ResponsiveLayout.CardWidth(104, 0, double.PositiveInfinity));
    }

    [Theory]
    [InlineData(232, 36, 196)]
    [InlineData(232, 300, ResponsiveLayout.MinimumCardTextWidth)]
    public void Card_text_is_measured_against_the_room_it_actually_has(
        double item, double decoration, double expected) =>
        Assert.Equal(expected, ResponsiveLayout.CardTextWidth(item, decoration));

    /// <summary>
    /// The height that goes with a width in the supported matrix.
    ///
    /// The shortest supported window is 560, and the tightest real
    /// configurations are 600 epx tall, so the pessimistic pairing is the
    /// honest one to test against.
    /// </summary>
    private static double EffectiveHeightFor(double effectiveWidth) =>
        effectiveWidth < 1000 ? 560 : effectiveWidth * 9 / 16;
}
