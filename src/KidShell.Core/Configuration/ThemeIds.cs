namespace KidShell.Core.Configuration;

/// <summary>
/// Child Mode scene themes. The ids are what reach the configuration file; the
/// Swedish labels and the artwork live in the App layer.
/// </summary>
public static class ThemeIds
{
    /// <summary>Skogen.</summary>
    public const string Forest = "forest";

    /// <summary>Rymden.</summary>
    public const string Space = "space";

    /// <summary>Havet.</summary>
    public const string Ocean = "ocean";

    /// <summary>Dinosaurier.</summary>
    public const string Dino = "dino";

    /// <summary>Färgglatt.</summary>
    public const string Bright = "bright";

    public const string Default = Forest;

    public static readonly string[] All = [Forest, Space, Ocean, Dino, Bright];

    public static bool IsKnown(string? id) =>
        !string.IsNullOrWhiteSpace(id) && All.Contains(id, StringComparer.Ordinal);

    /// <summary>
    /// Whether the scene is dark enough that text drawn straight onto it has
    /// to switch to a light palette. Contrast is a property of the theme, so
    /// it is declared next to the theme rather than guessed by the UI.
    /// </summary>
    public static bool IsDarkScene(string? id) =>
        string.Equals(Migrate(id), Space, StringComparison.Ordinal);

    /// <summary>
    /// Maps the MVP 0.1 theme ids onto the current set. "meadow" and "sunset"
    /// predate the five named themes; they are translated rather than dropped
    /// so an existing development configuration keeps a sensible look.
    /// </summary>
    public static string Migrate(string? id) => id switch
    {
        null or "" => Default,
        "meadow" => Forest,
        "sunset" => Bright,
        _ when IsKnown(id) => id!,
        _ => Default
    };
}
