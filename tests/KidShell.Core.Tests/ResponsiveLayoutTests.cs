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
}
