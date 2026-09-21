using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels;

public enum ShellMode
{
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
    private readonly IKidShellLogger _logger;

    private ShellMode _mode = ShellMode.Child;
    private bool _isPinOpen;

    public ShellViewModel(
        IAppStateService state,
        IDialogService dialogs,
        IDeveloperOptions developerOptions,
        IKidShellLogger logger,
        ChildHomeViewModel child,
        PinOverlayViewModel pin,
        ParentShellViewModel parent)
    {
        _state = state;
        _dialogs = dialogs;
        _developerOptions = developerOptions;
        _logger = logger;

        Child = child;
        Pin = pin;
        Parent = parent;

        Pin.Accepted += (_, _) => OpenParentMode();
        Pin.Cancelled += (_, _) => ClosePin();
        Parent.BackToChildRequested += (_, _) => ReturnToChild();
        Parent.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        RequestParentAccessCommand = new RelayCommand(OpenPin);
    }

    public ChildHomeViewModel Child { get; }

    public PinOverlayViewModel Pin { get; }

    public ParentShellViewModel Parent { get; }

    public RelayCommand RequestParentAccessCommand { get; }

    /// <summary>Raised when KidShell should close (developer-mode exit).</summary>
    public event EventHandler? ExitRequested;

    public bool DeveloperMode => _developerOptions.DeveloperMode;

    public string DeveloperBadge => Strings.Get("Dev.Badge");

    public string DeveloperShortcutHint => Strings.Get("Dev.ParentShortcut");

    public ShellMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsChildMode));
                OnPropertyChanged(nameof(IsParentMode));
            }
        }
    }

    public bool IsChildMode => Mode == ShellMode.Child;

    public bool IsParentMode => Mode == ShellMode.Parent;

    public bool IsPinOpen
    {
        get => _isPinOpen;
        private set => SetProperty(ref _isPinOpen, value);
    }

    /// <summary>Shows the PIN gate. The only route from Child Mode to Parent Mode.</summary>
    public void OpenPin()
    {
        if (IsParentMode)
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
