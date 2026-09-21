using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.Onboarding;

public enum OnboardingCompletion
{
    /// <summary>The profile was written and onboarding is now complete.</summary>
    Completed = 0,

    /// <summary>Something was still missing; nothing was written.</summary>
    Incomplete = 1,

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
    private readonly IKidShellLogger _logger;

    public OnboardingService(IAppStateService state, IKidShellLogger logger)
    {
        _state = state;
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

        // Commit through the ordinary state service so Child Mode is rebuilt by
        // the same ConfigurationChanged path as any other saved change.
        var config = _state.CreateDraft();
        config.Child = draft.ToProfile();

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
