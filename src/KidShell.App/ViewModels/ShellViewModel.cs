using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Mvvm;
using KidShell.Core.ScreenTime;
using KidShell.Core.Runtime;
using KidShell.Core.Security;

namespace KidShell.App.ViewModels;

public enum ShellMode
{
    /// <summary>First-run setup. Shown until a parent has configured a child.</summary>
    Onboarding,
    Child,
    Parent
}

/// <summary>
/// Top-level application state: which face KidShell is showing, and whether
/// the PIN gate is up.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly IAppStateService _state;
    private readonly IDialogService _dialogs;
    private readonly IDeveloperOptions _developerOptions;
    private readonly IParentPinService _pinService;
    private readonly IScreenTimeCoordinator _screenTime;
    private readonly IKidShellLogger _logger;
    private readonly IParentSession _parentSession;
    private readonly IUiDispatcher _ui;

    private ShellMode _mode = ShellMode.Child;
    private bool _isPinOpen;

    public ShellViewModel(
        IAppStateService state,
        IDialogService dialogs,
        IDeveloperOptions developerOptions,
        IParentPinService pinService,
        IParentSession parentSession,
        IScreenTimeCoordinator screenTime,
        IUiDispatcher ui,
        IKidShellLogger logger,
        OnboardingViewModel onboarding,
        ChildHomeViewModel child,
        PinOverlayViewModel pin,
        ParentShellViewModel parent)
    {
        _state = state;
        _dialogs = dialogs;
        _developerOptions = developerOptions;
        _pinService = pinService;
        _parentSession = parentSession;
        _screenTime = screenTime;
        _ui = ui;
        _logger = logger;

        Onboarding = onboarding;
        Child = child;
        Pin = pin;
        Parent = parent;

        Pin.Accepted += (_, _) => OpenParentMode();

        // Parent Mode used to stay open until somebody closed it, which on a
        // machine the child also uses means it stayed open. The session
        // latches the door behind the PIN.
        _parentSession.Ended += (_, reason) =>
        {
            if (IsParentMode)
            {
                _logger.Info("Shell", $"Parent Mode re-locked ({reason}).");
                ReturnToChild();
            }
        };

        // Evaluated on the screen-time tick rather than on a timer of its own.
        // Something is already waking up every thirty seconds, and a second
        // timer for the same job is a second thing to get wrong.
        //
        // Marshalled, because that tick is a thread-pool callback and
        // expiring the session leaves Parent Mode - which is a UI change. See
        // IUiDispatcher; this is the same boundary, reached from a different
        // direction.
        _screenTime.Changed += (_, _) => _ui.Post(_parentSession.Evaluate);
        Pin.Cancelled += (_, _) => ClosePin();
        Parent.BackToChildRequested += (_, _) => ReturnToChild();
        Parent.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        Parent.RestartOnboardingRequested += (_, _) => StartOnboarding();

        // Anything the parent does in Parent Mode restarts the idle window.
        // Measured from the last action rather than from when the session
        // began, so a long setup pass is not interrupted mid-sentence.
        Parent.PropertyChanged += (_, _) => _parentSession.Touch();
        Onboarding.Completed += (_, _) => FinishOnboarding();

        // The setup screens preview the chosen theme live, so a pick on the
        // theme step has to reach the window's scene background.
        Onboarding.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OnboardingViewModel.PreviewThemeId) && IsOnboardingMode)
            {
                OnPropertyChanged(nameof(SceneThemeId));
            }
        };

        RequestParentAccessCommand = new RelayCommand(OpenPin);

        // Deterministic startup routing: a configuration without a finished
        // child profile always lands on first-run setup.
        _mode = state.Current.RequiresOnboarding ? ShellMode.Onboarding : ShellMode.Child;

        if (_mode == ShellMode.Onboarding)
        {
            Onboarding.Reset();
        }
        else
        {
            // Time starts counting the moment a configured child sees their
            // screen - not when Parent Mode is opened, and not during setup.
            _screenTime.Start();
        }
    }

    public OnboardingViewModel Onboarding { get; }

    public ChildHomeViewModel Child { get; }

    public PinOverlayViewModel Pin { get; }

    public ParentShellViewModel Parent { get; }

    public RelayCommand RequestParentAccessCommand { get; }

    /// <summary>Raised when KidShell should close (developer-mode exit).</summary>
    public event EventHandler? ExitRequested;

    public bool DeveloperMode => _developerOptions.DeveloperMode;

    /// <summary>
    /// Whether the published fallback PIN would currently open Parent Mode.
    /// Surfaced in the child's footer badge because it is the state that
    /// actually matters, not the build flavour.
    /// </summary>
    public bool DevelopmentPinActive => _pinService.IsDevelopmentFallbackActive;

    public string DeveloperBadge => Strings.Get("Dev.Badge");

    public string DeveloperShortcutHint => Strings.Get("Dev.ParentShortcut");

    public ShellMode Mode
    {
        get => _mode;

        // internal, not private: the layout audit drives the shell through
        // every mode, and going the long way round - finishing setup to reach
        // Child Mode - would make the audit depend on the very screens it is
        // supposed to be measuring.
        internal set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsOnboardingMode));
                OnPropertyChanged(nameof(IsChildMode));
                OnPropertyChanged(nameof(IsParentMode));
                OnPropertyChanged(nameof(SceneThemeId));
            }
        }
    }

    public bool IsOnboardingMode => Mode == ShellMode.Onboarding;

    public bool IsChildMode => Mode == ShellMode.Child;

    public bool IsParentMode => Mode == ShellMode.Parent;

    /// <summary>
    /// The scene theme to draw behind whatever is on screen. During setup the
    /// parent's current pick previews live; afterwards it follows the profile.
    /// </summary>
    public string SceneThemeId =>
        IsOnboardingMode ? Onboarding.PreviewThemeId : _state.Current.Child.ThemeId;

    public bool IsPinOpen
    {
        get => _isPinOpen;
        private set => SetProperty(ref _isPinOpen, value);
    }

    /// <summary>Shows the PIN gate. The only route from Child Mode to Parent Mode.</summary>
    public void OpenPin()
    {
        if (IsParentMode || IsOnboardingMode)
        {
            return;
        }

        Pin.Reset();
        IsPinOpen = true;
        _logger.Info("Shell", "Parent PIN prompt opened.");
    }

    public void ClosePin() => IsPinOpen = false;

    /// <summary>Called after a correct PIN and never directly from the UI.</summary>
    private void OpenParentMode()
    {
        IsPinOpen = false;
        Parent.Reset();
        _parentSession.Begin();
        Mode = ShellMode.Parent;
        _logger.Info("Shell", "Parent Mode opened.");
    }

    private void ReturnToChild()
    {
        // Ended before the mode changes, so a session cannot outlive the
        // screen it belongs to whichever way the parent left.
        _parentSession.End(ParentSessionEndReason.ReturnedToChild);

        Mode = ShellMode.Child;
        Child.Refresh();
        _logger.Info("Shell", "Returned to Child Mode.");
    }

    /// <summary>Sends the app back to first-run setup with a clean draft.</summary>
    public void StartOnboarding()
    {
        IsPinOpen = false;
        Onboarding.Reset();
        Mode = ShellMode.Onboarding;
        _logger.Info("Shell", "First-run setup opened.");
    }

    /// <summary>Called once the profile has been saved by the setup flow.</summary>
    private void FinishOnboarding()
    {
        Child.Refresh();
        Mode = ShellMode.Child;

        // The counter was deliberately not running during setup: a parent
        // spending twenty minutes choosing a theme must not spend the child's
        // allowance doing it.
        _screenTime.Start();

        OnPropertyChanged(nameof(SceneThemeId));
        _logger.Info("Shell", "First-run setup finished; Child Mode is now personalised.");
    }

    /// <summary>
    /// Surfaces a one-time notice when the configuration file had to be
    /// rebuilt from defaults, instead of failing silently.
    /// </summary>
    public async Task ReportStartupIssuesAsync()
    {
        if (_state.LoadStatus != ConfigurationLoadStatus.RecoveredFromCorruption)
        {
            return;
        }

        await _dialogs.ShowMessageAsync(
            Strings.Get("Config.RecoveredTitle"),
            Strings.Get("Config.RecoveredBody"));
    }
}
