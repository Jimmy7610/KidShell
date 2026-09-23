using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.ViewModels.Parent;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Mvvm;
using KidShell.Core.Onboarding;
using KidShell.Core.Security;
using KidShell.Core.Runtime;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.Core.Sessions;

namespace KidShell.App.ViewModels;

public enum ParentPage
{
    Overview,
    Apps,
    ScreenTime,
    Web,
    Security,
    Profile
}

/// <summary>
/// Föräldraläge.
///
/// Parent Mode edits a detached draft of the configuration. Nothing reaches
/// Child Mode until "Spara ändringar" commits the draft, and leaving with
/// unsaved work asks first.
/// </summary>
public sealed class ParentShellViewModel : ObservableObject
{
    private readonly IAppStateService _state;
    private readonly IDialogService _dialogs;
    private readonly IParentPinService _pinService;
    private readonly IPinChangeFlow _pinChangeFlow;
    private readonly IOnboardingService _onboarding;
    private readonly IDeveloperOptions _developerOptions;
    private readonly ISessionController _sessions;
    private readonly IKidShellLogger _logger;

    private KidShellConfiguration _draft;
    private ParentPage _selectedPage = ParentPage.Overview;
    private bool _hasUnsavedChanges;
    private string? _statusMessage;

    public ParentShellViewModel(
        IAppStateService state,
        IDialogService dialogs,
        IParentPinService pinService,
        IAddAppFlow addAppFlow,
        IPinChangeFlow pinChangeFlow,
        IOnboardingService onboarding,
        ISecurityReadinessService readiness,
        ISecurityDialogs securityDialogs,
        IDeveloperOptions developerOptions,
        IRuntimeEnvironment runtime,
        ScreenTimeEngine screenTimeEngine,
        ISessionController sessions,
        IRecoveryManifestStore recoveryStore,
        IKidShellLogger logger)
    {
        _state = state;
        _dialogs = dialogs;
        _pinService = pinService;
        _pinChangeFlow = pinChangeFlow;
        _onboarding = onboarding;
        _developerOptions = developerOptions;
        _sessions = sessions;
        _logger = logger;

        _draft = state.CreateDraft();

        Overview = new ParentOverviewViewModel();
        Apps = new ParentAppsViewModel(addAppFlow, MarkDirty);
        ScreenTime = new ParentScreenTimeViewModel(MarkDirty, screenTimeEngine, state);
        Web = new ParentWebViewModel(MarkDirty);
        Security = new ParentSecurityViewModel(pinService, developerOptions, readiness, securityDialogs);
        Profile = new ParentProfileViewModel(MarkDirty, () => _ = RerunOnboardingAsync());
        About = new AboutViewModel(runtime);
        Recovery = new RecoveryStatusViewModel(recoveryStore);

        // One scan feeds both surfaces: Säkerhet shows the detail, Om KidShell
        // shows the summary, and neither reads the machine twice.
        Security.ReportChanged += (_, capabilities) => About.Load(capabilities);

        SelectPageCommand = new RelayCommand(parameter =>
        {
            if (parameter is ParentPage page)
            {
                SelectedPage = page;
            }
            else if (parameter is string name && Enum.TryParse<ParentPage>(name, ignoreCase: true, out var parsed))
            {
                SelectedPage = parsed;
            }
        });

        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        BackCommand = new RelayCommand(() => _ = BackAsync());
        ExitCommand = new RelayCommand(() => _ = ExitAsync());
        ChangePinCommand = new RelayCommand(() => _ = ChangePinAsync());

        Reset();
    }

    public ParentOverviewViewModel Overview { get; }

    public ParentAppsViewModel Apps { get; }

    public ParentScreenTimeViewModel ScreenTime { get; }

    /// <summary>Om KidShell: version, build and what this machine can do.</summary>
    public AboutViewModel About { get; }

    /// <summary>What KidShell has changed, and whether any of it needs a human.</summary>
    public RecoveryStatusViewModel Recovery { get; }

    public ParentWebViewModel Web { get; }

    public ParentSecurityViewModel Security { get; }

    public ParentProfileViewModel Profile { get; }

    public RelayCommand SelectPageCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand ChangePinCommand { get; }

    /// <summary>Raised when Parent Mode should close and Child Mode return.</summary>
    public event EventHandler? BackToChildRequested;

    /// <summary>Raised when the parent confirmed "Avsluta till Windows".</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the child profile was cleared and setup should run again.</summary>
    public event EventHandler? RestartOnboardingRequested;

    public bool DeveloperMode => _developerOptions.DeveloperMode;

    public string ChildSummary => Strings.Format("Parent.ChildSummary", _draft.Child.Name, _draft.Child.Age);

    public string AvatarId => _draft.Child.AvatarId;

    public bool IsPinConfigured => _pinService.IsCustomPinConfigured;

    public ParentPage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (SetProperty(ref _selectedPage, value))
            {
                NotifyPageSelection();

                if (value == ParentPage.Overview)
                {
                    Overview.Refresh();
                }
            }
        }
    }

    public bool IsOverviewSelected => SelectedPage == ParentPage.Overview;

    public bool IsAppsSelected => SelectedPage == ParentPage.Apps;

    public bool IsScreenTimeSelected => SelectedPage == ParentPage.ScreenTime;

    public bool IsWebSelected => SelectedPage == ParentPage.Web;

    public bool IsSecuritySelected => SelectedPage == ParentPage.Security;

    public bool IsProfileSelected => SelectedPage == ParentPage.Profile;

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Starts a fresh editing session from the live configuration.</summary>
    public void Reset()
    {
        _draft = _state.CreateDraft();
        StatusMessage = null;

        Overview.Load(_draft, Security.OverviewSummary);
        Apps.Load(_draft);
        ScreenTime.Load(_draft);
        Web.Load(_draft);
        Profile.Load(_draft);
        Security.Refresh();

        HasUnsavedChanges = false;
        SelectedPage = ParentPage.Overview;

        OnPropertyChanged(nameof(ChildSummary));
        OnPropertyChanged(nameof(AvatarId));
        OnPropertyChanged(nameof(IsPinConfigured));
        NotifyPageSelection();

        _ = RunSecurityScanAsync();
    }

    /// <summary>
    /// Runs the read-only readiness scan whenever Parent Mode opens, so the
    /// Säkerhet page and the Översikt summary show live machine state rather
    /// than a stale or hard-coded verdict.
    ///
    /// Read-only: the scan detects, evaluates and plans. It cannot change a
    /// Windows setting, and it does not touch KidShell's configuration either,
    /// so it is safe to run while a draft is being edited.
    /// </summary>
    private async Task RunSecurityScanAsync()
    {
        try
        {
            await Security.RefreshAsync();
        }
        catch (Exception ex)
        {
            // A failed scan degrades the security page; it never stops a
            // parent getting to the rest of Parent Mode.
            _logger.Error("Parent", "The security readiness scan failed.", ex);
            return;
        }

        Overview.Load(_draft, Security.OverviewSummary);
    }

    private void MarkDirty()
    {
        // Compare against live state rather than trusting a flag: editing a
        // value and editing it back is genuinely not a change.
        HasUnsavedChanges = !ConfigurationSnapshot.AreEquivalent(_draft, _state.Current);
        StatusMessage = null;

        Overview.Refresh();
        OnPropertyChanged(nameof(ChildSummary));
        OnPropertyChanged(nameof(AvatarId));
    }

    private async Task<bool> SaveAsync()
    {
        if (_state.Commit(_draft))
        {
            _logger.Info("Parent", "Configuration committed from Parent Mode.");

            // Continue editing on a fresh draft of the now-live configuration.
            _draft = _state.CreateDraft();
            Overview.Load(_draft, Security.OverviewSummary);
            Apps.Load(_draft);
            ScreenTime.Load(_draft);
            Web.Load(_draft);
            Profile.Load(_draft);

            HasUnsavedChanges = false;
            StatusMessage = Strings.Get("Parent.Saved");
            return true;
        }

        StatusMessage = Strings.Get("Parent.SaveFailed");
        await _dialogs.ShowMessageAsync(Strings.Get("Parent.Title"), Strings.Get("Parent.SaveFailed"));
        return false;
    }

    private async Task BackAsync()
    {
        if (!HasUnsavedChanges)
        {
            BackToChildRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var choice = await _dialogs.ShowConfirmAsync(
            Strings.Get("Dialog.DiscardTitle"),
            Strings.Get("Dialog.DiscardBody"),
            Strings.Get("Dialog.DiscardPrimary"),
            Strings.Get("Dialog.DiscardSecondary"));

        switch (choice)
        {
            case ConfirmChoice.Primary:
                Reset();
                BackToChildRequested?.Invoke(this, EventArgs.Empty);
                break;

            case ConfirmChoice.Secondary:
                if (await SaveAsync())
                {
                    BackToChildRequested?.Invoke(this, EventArgs.Empty);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Avsluta barnläget.
    ///
    /// WHAT THIS MUST DO, AND WHY IT DEPENDS
    /// -------------------------------------
    /// On a machine with a verified secure configuration, ending Child Mode has
    /// to sign the child's Windows session out. Merely closing KidShell would
    /// drop the child onto the desktop of an account that is still signed in -
    /// the exact opposite of what a parent pressing that button intends, and a
    /// way out of KidShell that a child would find within a week.
    ///
    /// On a development machine it must do no such thing. A developer who loses
    /// their session to a button they were testing has lost their work.
    ///
    /// The controller decides which, and the confirmation text says which is
    /// about to happen rather than describing the one that sounds better.
    /// </summary>
    private async Task ExitAsync()
    {
        if (HasUnsavedChanges)
        {
            // Signing out with unsaved edits would discard them silently.
            var unsaved = await _dialogs.ShowConfirmAsync(
                Strings.Get("Dialog.ExitTitle"),
                Strings.Get("Dialog.ExitUnsaved"),
                Strings.Get("Dialog.ExitDiscard"),
                cancelText: Strings.Get("Dialog.ExitStay"));

            if (unsaved != ConfirmChoice.Primary)
            {
                return;
            }
        }

        var willSignOut = !_sessions.IsSimulated;

        var choice = await _dialogs.ShowConfirmAsync(
            Strings.Get("Dialog.ExitTitle"),
            willSignOut
                ? Strings.Get("Dialog.ExitBodySignOut")
                : Strings.Get("Dialog.ExitBodyDeveloper"),
            willSignOut
                ? Strings.Get("Dialog.ExitPrimarySignOut")
                : Strings.Get("Dialog.ExitPrimary"));

        if (choice != ConfirmChoice.Primary)
        {
            return;
        }

        var result = await _sessions
            .PerformAsync(willSignOut ? SessionAction.SignOut : SessionAction.CloseKidShell)
            .ConfigureAwait(true);

        _logger.Info("Session", $"Parent ended Child Mode: {result.Description}");

        if (!result.Performed)
        {
            // Simulated, or it failed. Either way the honest fallback is to
            // close KidShell, and the parent is told which happened.
            if (willSignOut)
            {
                await _dialogs.ShowMessageAsync(
                    Strings.Get("Dialog.ExitTitle"),
                    Strings.Get("Dialog.ExitSignOutFailed"));
            }

            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Windows is tearing the session down; there is nothing further to do.
    }

    /// <summary>
    /// Clears the child profile and hands control back to first-run setup.
    /// Deliberately keeps the app catalogue: this is also how a parent hands
    /// the computer to a different child.
    /// </summary>
    private async Task RerunOnboardingAsync()
    {
        if (HasUnsavedChanges)
        {
            // Restarting commits through the same store, so unsaved edits would
            // be silently lost. Ask the parent to resolve them first.
            await _dialogs.ShowMessageAsync(
                Strings.Get("Profile.RerunTitle"),
                Strings.Get("Profile.RerunUnsaved"));
            return;
        }

        var choice = await _dialogs.ShowConfirmAsync(
            Strings.Get("Profile.RerunTitle"),
            Strings.Get("Profile.RerunBody"),
            Strings.Get("Profile.RerunPrimary"));

        if (choice != ConfirmChoice.Primary)
        {
            return;
        }

        if (!_onboarding.Restart())
        {
            await _dialogs.ShowMessageAsync(
                Strings.Get("Profile.RerunTitle"),
                Strings.Get("Profile.RerunFailed"));
            return;
        }

        _logger.Info("Parent", "Child profile cleared; first-run setup will run again.");
        Reset();
        RestartOnboardingRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ChangePinAsync()
    {
        var pin = await _pinChangeFlow.RequestNewPinAsync();
        if (pin is null)
        {
            return;
        }

        if (!_pinService.TrySetPin(pin))
        {
            await _dialogs.ShowMessageAsync(
                Strings.Get("Dialog.ChangePinTitle"),
                Strings.Get("Dialog.ChangePinInvalid"));
            return;
        }

        // The PIN is stored on the live configuration, so bring the draft's
        // copy in line to avoid a spurious "unsaved changes".
        _draft.ParentPin = _state.Current.ParentPin.Clone();
        HasUnsavedChanges = !ConfigurationSnapshot.AreEquivalent(_draft, _state.Current);

        Security.Refresh();
        OnPropertyChanged(nameof(IsPinConfigured));

        await _dialogs.ShowMessageAsync(
            Strings.Get("Dialog.ChangePinTitle"),
            Strings.Get("Dialog.ChangePinSaved"));
    }

    private void NotifyPageSelection()
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsAppsSelected));
        OnPropertyChanged(nameof(IsScreenTimeSelected));
        OnPropertyChanged(nameof(IsWebSelected));
        OnPropertyChanged(nameof(IsSecuritySelected));
        OnPropertyChanged(nameof(IsProfileSelected));
    }
}
