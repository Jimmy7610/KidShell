using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Mvvm;
using KidShell.Core.ScreenTime;
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

    private ShellMode _mode = ShellMode.Child;
    private bool _isPinOpen;

    public ShellViewModel(
        IAppStateService state,
        IDialogService dialogs,
        IDeveloperOptions developerOptions,
        IParentPinService pinService,
        IScreenTimeCoordinator screenTime,
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
        _screenTime = screenTime;
        _logger = logger;

        Onboarding = onboarding;
        Child = child;
        Pin = pin;
        Parent = parent;

        Pin.Accepted += (_, _) => OpenParentMode();
        Pin.Cancelled += (_, _) => ClosePin();
        Parent.BackToChildRequested += (_, _) => ReturnToChild();
        Parent.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        Parent.RestartOnboardingRequested += (_, _) => StartOnboarding();
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
        private set
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
        Mode = ShellMode.Parent;
        _logger.Info("Shell", "Parent Mode opened.");
    }

    private void ReturnToChild()
    {
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
