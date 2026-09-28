using KidShell.Core.Configuration;
using KidShell.Core.Runtime;
using KidShell.Core.Security;

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

    /// <summary>
    /// The parent PIN chosen during setup, held only for the length of the
    /// session and never persisted in this form - Complete() hashes it and the
    /// draft is discarded. Null means the parent has not set one yet.
    /// </summary>
    public string? ParentPin { get; set; }

    /// <summary>Whether the chosen PIN passes policy.</summary>
    public bool HasParentPin => ParentPin is not null &&
                                ParentPinPolicy.Validate(ParentPin) == PinValidation.Ok;

    public bool HasName => ValidateName(Name) == NameValidation.Ok;

    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarId);

    public bool HasAge => Age >= ChildProfile.MinAge;

    public bool HasTheme => ThemeIds.IsKnown(ThemeId);

    // ------------------------------------------------------------- rules
    // Collected during setup so a parent does not finish first-run with a
    // machine that has no limits at all and no prompt to add any. Every value
    // has a workable default, so a parent who accepts the suggestion gets
    // something sensible rather than nothing.

    /// <summary>Whether screen time is switched on for the child.</summary>
    public bool ScreenTimeEnabled { get; set; } = true;

    public int WeekdayMinutes { get; set; } = 60;

    public int WeekendMinutes { get; set; } = 120;

    /// <summary>How much of the web the child may reach.</summary>
    public WebMode WebMode { get; set; } = WebMode.NoBrowser;

    /// <summary>
    /// Whether the profile half of setup is finished. A production build
    /// additionally requires a parent PIN - see
    /// <see cref="IsCompleteFor"/>.
    /// </summary>
    public bool IsComplete => HasName && HasAvatar && HasAge && HasTheme;

    /// <summary>
    /// Whether setup can finish in the given build.
    ///
    /// A production build cannot complete without a real parent PIN: finishing
    /// without one would leave Parent Mode either unreachable or - worse, if
    /// the fallback were ever reinstated - open to anyone who read the
    /// documentation.
    /// </summary>
    public bool IsCompleteFor(KidShellRuntimeMode mode) =>
        IsComplete && (mode == KidShellRuntimeMode.Development || HasParentPin);

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
