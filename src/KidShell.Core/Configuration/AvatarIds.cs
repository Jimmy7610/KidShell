namespace KidShell.Core.Configuration;

/// <summary>
/// Avatar ids offered during onboarding and in Parent Mode. The artwork is
/// vector XAML in the App layer (Themes/Avatars.xaml); the ids are what reach
/// the configuration file.
/// </summary>
public static class AvatarIds
{
    public const string Fox = "fox";
    public const string Owl = "owl";
    public const string Bear = "bear";
    public const string Panda = "panda";
    public const string Rocket = "rocket";
    public const string Blossom = "blossom";
    public const string Cat = "cat";
    public const string Dino = "dino";
    public const string Robot = "robot";
    public const string Whale = "whale";
    public const string Lion = "lion";
    public const string Frog = "frog";
    public const string Bee = "bee";
    public const string Star = "star";

    /// <summary>All avatars, in the order they are offered.</summary>
    public static readonly string[] All =
    [
        Fox, Owl, Bear, Panda, Cat, Lion,
        Frog, Bee, Whale, Dino, Robot, Rocket,
        Blossom, Star
    ];

    public static bool IsKnown(string? id) =>
        !string.IsNullOrWhiteSpace(id) && All.Contains(id, StringComparer.Ordinal);
}
