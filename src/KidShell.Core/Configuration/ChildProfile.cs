namespace KidShell.Core.Configuration;

/// <summary>The child KidShell is set up for.</summary>
public sealed class ChildProfile
{
    public string Name { get; set; } = "Alice";

    public int Age { get; set; } = 6;

    /// <summary>Key into the App layer avatar dictionary (Themes/Avatars.xaml).</summary>
    public string AvatarId { get; set; } = "fox";

    /// <summary>Key into the App layer theme table.</summary>
    public string ThemeId { get; set; } = "meadow";

    public ChildProfile Clone() => new()
    {
        Name = Name,
        Age = Age,
        AvatarId = AvatarId,
        ThemeId = ThemeId
    };
}
