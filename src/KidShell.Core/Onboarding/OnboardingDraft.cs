using KidShell.Core.Configuration;

namespace KidShell.Core.Onboarding;

/// <summary>Why a name was rejected. The wording lives in the App layer.</summary>
public enum NameValidation
{
    Ok = 0,
    Empty = 1,
    TooLong = 2
}

/// <summary>
/// What a parent has chosen so far during first-run setup.
///
/// This is a working copy only. Nothing here reaches the configuration file
/// until <see cref="IOnboardingService.Complete"/> succeeds, which is what
/// makes "closed halfway through setup" safe: there is simply nothing to
/// half-save.
/// </summary>
public sealed class OnboardingDraft
{
    private string _name = string.Empty;

    /// <summary>The child's name, always stored trimmed.</summary>
    public string Name
    {
        get => _name;
        set => _name = (value ?? string.Empty).Trim();
    }

    /// <summary>Zero until the parent picks an age.</summary>
    public int Age { get; set; }

    public string AvatarId { get; set; } = string.Empty;

    public string ThemeId { get; set; } = ThemeIds.Default;

    public bool HasName => ValidateName(Name) == NameValidation.Ok;

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarId);

    public bool HasAge => Age >= ChildProfile.MinAge;

    public bool HasTheme => ThemeIds.IsKnown(ThemeId);

    public bool IsComplete => HasName && HasAvatar && HasAge && HasTheme;

    /// <summary>
    /// Validates a name as typed. Swedish and any other Unicode letters are
    /// fine; only emptiness and absurd length are rejected.
    /// </summary>
    public static NameValidation ValidateName(string? candidate)
    {
        var trimmed = (candidate ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return NameValidation.Empty;
        }

        return trimmed.Length > ChildProfile.MaxNameLength
            ? NameValidation.TooLong
            : NameValidation.Ok;
    }

    public ChildProfile ToProfile() => new()
    {
        Name = Name,
        Age = Age,
        AvatarId = AvatarId,
        ThemeId = ThemeIds.IsKnown(ThemeId) ? ThemeId : ThemeIds.Default,
        IsOnboardingComplete = true
    };
}
