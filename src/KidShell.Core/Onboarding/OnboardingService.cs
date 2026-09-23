using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using KidShell.Core.Security;

namespace KidShell.Core.Onboarding;

public enum OnboardingCompletion
{
    /// <summary>The profile was written and onboarding is now complete.</summary>
    Completed = 0,

    /// <summary>Something was still missing; nothing was written.</summary>
    Incomplete = 1,

    /// <summary>
    /// A production build was asked to finish setup without a parent PIN.
    /// Nothing was written.
    /// </summary>
    ParentPinRequired = 3,

    /// <summary>The configuration could not be persisted; nothing was changed.</summary>
    SaveFailed = 2
}

/// <summary>
/// Owns the first-run profile flow.
///
/// Onboarding writes through the ordinary configuration service - there is no
/// separate settings store - and the completion flag is only ever set by
/// <see cref="Complete"/>, after the parent confirms the final screen.
/// </summary>
public interface IOnboardingService
{
    /// <summary>True when KidShell must run first-run setup before Child Mode.</summary>
    bool RequiresOnboarding { get; }

    /// <summary>A fresh draft. Never pre-filled from an existing profile.</summary>
    OnboardingDraft CreateDraft();

    /// <summary>Validates the draft, writes the profile and marks onboarding complete.</summary>
    OnboardingCompletion Complete(OnboardingDraft draft);

    /// <summary>
    /// Clears the child profile and the completion flag so setup runs again.
    /// Apps, screen time, web settings and the parent PIN are left untouched.
    /// </summary>
    bool Restart();
}

public sealed class OnboardingService : IOnboardingService
{
    private readonly IAppStateService _state;
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;

    public OnboardingService(IAppStateService state, IRuntimeEnvironment environment, IKidShellLogger logger)
    {
        _state = state;
        _environment = environment;
        _logger = logger;
    }

    public bool RequiresOnboarding => _state.Current.RequiresOnboarding;

    public OnboardingDraft CreateDraft() => new();

    public OnboardingCompletion Complete(OnboardingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!draft.IsComplete)
        {
            _logger.Warning("Onboarding", "Completion refused: the setup draft is not finished.");
            return OnboardingCompletion.Incomplete;
        }

        if (!draft.IsCompleteFor(_environment.Mode))
        {
            _logger.Warning("Onboarding", "Completion refused: a production build requires a parent PIN.");
            return OnboardingCompletion.ParentPinRequired;
        }

        // Commit through the ordinary state service so Child Mode is rebuilt by
        // the same ConfigurationChanged path as any other saved change.
        var config = _state.CreateDraft();
        config.Child = draft.ToProfile();

        // The rules the parent chose during setup. Applied here rather than
        // left for them to discover in Parent Mode: finishing first-run with a
        // machine that has no limits and no prompt to add any is how a
        // parental-control product ends up unused.
        config.ScreenTime.IsEnabled = draft.ScreenTimeEnabled;
        config.ScreenTime.WeekdayMinutes = draft.WeekdayMinutes;
        config.ScreenTime.WeekendMinutes = draft.WeekendMinutes;
        config.Web.Mode = draft.WebMode;

        // The PIN is hashed here and the plaintext never leaves the draft,
        // which is discarded with the setup session.
        if (draft.ParentPin is { } pin && ParentPinPolicy.Validate(pin) == PinValidation.Ok)
        {
            var (hash, salt) = PinHasher.Hash(pin);
            config.ParentPin.Hash = hash;
            config.ParentPin.Salt = salt;
            config.ParentPin.Iterations = PinHasher.DefaultIterations;
        }

        if (!_state.Commit(config))
        {
            _logger.Error("Onboarding", "Completion failed: the configuration could not be saved.");
            return OnboardingCompletion.SaveFailed;
        }

        _logger.Info("Onboarding", "First-run setup completed; the child profile is configured.");
        return OnboardingCompletion.Completed;
    }

    public bool Restart()
    {
        var config = _state.CreateDraft();
        var appCount = config.Apps.Count;

        // Only the personal details go. The app catalogue, screen-time and web
        // settings and the parent PIN all survive, because handing the machine
        // to another child should not mean rebuilding it from scratch.
        // The parent PIN deliberately survives: re-running setup is how a
        // parent hands the machine to another child, and clearing their PIN
        // would unlock Parent Mode at exactly the wrong moment.
        config.Child.ResetForOnboarding();

        if (!_state.Commit(config))
        {
            _logger.Error("Onboarding", "Could not restart first-run setup: the configuration was not saved.");
            return false;
        }

        _logger.Info("Onboarding", $"First-run setup will run again; {appCount} configured apps were kept.");
        return true;
    }
}
