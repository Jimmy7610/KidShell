using System.Text.Json.Serialization;

namespace KidShell.Core.Configuration;

/// <summary>
/// One launchable entry in the child's app grid. Everything the UI shows for a
/// card comes from here - the grid is never hard-coded in XAML.
/// </summary>
public sealed class KidAppDefinition
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Child-facing activity name, e.g. "Rita".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Parent-facing program name, e.g. "Paint". Falls back to
    /// <see cref="DisplayName"/> when not set, so a parent sees what will
    /// actually run while the child sees what they can do.
    /// </summary>
    public string ProgramName { get; set; } = string.Empty;

    /// <summary>Name to show in Parent Mode lists. Derived, never persisted.</summary>
    [JsonIgnore]
    public string EffectiveProgramName =>
        string.IsNullOrWhiteSpace(ProgramName) ? DisplayName : ProgramName;

    /// <summary>Short child-friendly description, also used as the parent-row subtitle.</summary>
    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    /// <summary>Key into the App layer icon dictionary (Themes/AppIcons.xaml).</summary>
    public string Icon { get; set; } = IconKeys.Generic;

    public AccentStyle AccentStyle { get; set; } = AccentStyle.Sky;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Executable, shell command or protocol activation string. May be empty,
    /// which means "not configured yet" rather than "broken".
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public KidAppDefinition Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ProgramName = ProgramName,
        Description = Description,
        Category = Category,
        Icon = Icon,
        AccentStyle = AccentStyle,
        IsEnabled = IsEnabled,
        ExecutablePath = ExecutablePath,
        Arguments = Arguments,
        SortOrder = SortOrder
    };
}

/// <summary>Icon keys understood by the App layer icon dictionary.</summary>
public static class IconKeys
{
    public const string Paint = "paint";
    public const string Games = "games";
    public const string Learn = "learn";
    public const string Calculator = "calculator";
    public const string Music = "music";
    public const string Reading = "reading";
    public const string Video = "video";
    public const string Blocks = "blocks";
    public const string Browser = "browser";
    public const string Generic = "generic";
}
