using KidShell.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.Themes;

/// <summary>
/// Bridge between the platform-neutral tokens in KidShell.Core (such as
/// <see cref="AccentStyle"/>) and the brushes defined in Themes/Tokens.xaml.
/// Keeping the mapping here means Core never learns about brushes and XAML
/// never learns about enums.
/// </summary>
public static class ThemeLookup
{
    public static Brush AccentBrush(AccentStyle style) =>
        Lookup($"Accent{style}Brush") as Brush ?? Fallback;

    public static Brush Brush(string key) => Lookup(key) as Brush ?? Fallback;

    /// <summary>Available avatar keys, in the order Parent Mode offers them.</summary>
    public static IReadOnlyList<string> AvatarIds { get; } =
        ["fox", "owl", "bear", "panda", "rocket", "blossom"];

    private static Brush Fallback { get; } = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue);

    private static object? Lookup(string key) =>
        Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value)
            ? value
            : null;
}
