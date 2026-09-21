using System.Text.Json.Serialization;

namespace KidShell.Core.Configuration;

/// <summary>
/// The child KidShell is set up for.
///
/// There is deliberately no default name, age or avatar. KidShell ships with an
/// empty profile and asks a parent to fill it in during first-run onboarding;
/// a pretend child on the first launch would be worse than an honest blank.
/// </summary>
public sealed class ChildProfile
{
    /// <summary>Longest name KidShell will store. Generous, but not unbounded.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Age used when the parent picks the open-ended "10+" choice.</summary>
    public const int OpenEndedAge = 10;

    public const int MinAge = 2;
    public const int MaxAge = 18;

    /// <summary>Empty until onboarding collects it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Zero means "not chosen yet".</summary>
    public int Age { get; set; }

    /// <summary>Empty until onboarding collects it.</summary>
    public string AvatarId { get; set; } = string.Empty;

    /// <summary>Key into the App layer theme table.</summary>
    public string ThemeId { get; set; } = ThemeIds.Default;

    /// <summary>True once a parent has finished first-run setup for this child.</summary>
    public bool IsOnboardingComplete { get; set; }

    /// <summary>
    /// Whether the profile actually holds everything Child Mode needs. Used
    /// alongside <see cref="IsOnboardingComplete"/> so that a half-written
    /// document can never produce a half-configured Child Mode.
    /// </summary>
    [JsonIgnore]
    public bool HasRequiredDetails =>
        !string.IsNullOrWhiteSpace(Name) &&
        Age > 0 &&
        !string.IsNullOrWhiteSpace(AvatarId);

    /// <summary>Clears the personal details without touching anything else.</summary>
    public void ResetForOnboarding()
    {
        Name = string.Empty;
        Age = 0;
        AvatarId = string.Empty;
        IsOnboardingComplete = false;
    }

    public ChildProfile Clone() => new()
    {
        Name = Name,
        Age = Age,
        AvatarId = AvatarId,
        ThemeId = ThemeId,
        IsOnboardingComplete = IsOnboardingComplete
    };
}
