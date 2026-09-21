using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using KidShell.Core.Security;
using KidShell.Core.Security.Readiness;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels.Parent;

public enum SecurityState
{
    Inactive,
    Active,
    Warning
}

/// <summary>A single honest line on the security page.</summary>
public sealed class SecurityStatusViewModel(string label, string value, SecurityState state, string? hint = null)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public SecurityState State { get; } = state;

    public string? Hint { get; } = hint;

    /// <summary>Glyph carries the state too: never colour alone.</summary>
    public string Glyph => State switch
    {
        SecurityState.Active => "",   // check mark
        SecurityState.Warning => "",  // warning
        _ => ""                        // cancel
    };

    public Brush StatusBrush => ThemeLookup.Brush(State switch
    {
        SecurityState.Active => "StatusOkBrush",
        SecurityState.Warning => "StatusWarningBrush",
        _ => "StatusInactiveBrush"
    });

    public string AutomationName => $"{Label}: {Value}";
}

/// <summary>One "label / value" line in the Windows information card.</summary>
public sealed class SecurityFactViewModel(string label, string value)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public string AutomationName => $"{Label}: {Value}";
}

/// <summary>One pre-flight check row.</summary>
public sealed class SecurityCheckViewModel
{
    public SecurityCheckViewModel(ReadinessCheck check)
    {
        Title = check.Title;
        Detail = check.Detail;
        Status = check.Status;
    }

    public string Title { get; }

    public string Detail { get; }

    public CheckStatus Status { get; }

    public string StatusLabel => Status switch
    {
        CheckStatus.Passed => Strings.Get("Security.CheckPassed"),
        CheckStatus.Warning => Strings.Get("Security.CheckWarning"),
        CheckStatus.Failed => Strings.Get("Security.CheckFailed"),
        _ => Strings.Get("Security.CheckNotApplicable")
    };

    /// <summary>Glyph as well as colour, so the state is never colour alone.</summary>
    public string Glyph => Status switch
    {
        CheckStatus.Passed => "",
        CheckStatus.Warning => "",
        CheckStatus.Failed => "",
        _ => ""
    };

    public Brush StatusBrush => ThemeLookup.Brush(Status switch
    {
        CheckStatus.Passed => "StatusOkBrush",
        CheckStatus.Warning => "StatusWarningBrush",
        CheckStatus.Failed => "StatusAlertBrush",
        _ => "StatusInactiveBrush"
    });

    public string AutomationName => $"{Title}: {StatusLabel}. {Detail}";
}

/// <summary>
/// Föräldraläge → Säkerhet.
///
/// A readiness dashboard driven by <see cref="ISecurityReadinessService"/>.
/// Every value shown here is read from the machine; nothing on this page can
/// change a Windows setting, and the page says so in as many words.
/// </summary>
public sealed class ParentSecurityViewModel : ObservableObject
{
    private readonly IParentPinService _pinService;
    private readonly IDeveloperOptions _developerOptions;
    private readonly ISecurityReadinessService _readiness;
    private readonly ISecurityDialogs _dialogs;

    private SecurityReadinessReport? _report;
    private bool _isScanning;

    public ParentSecurityViewModel(
        IParentPinService pinService,
        IDeveloperOptions developerOptions,
        ISecurityReadinessService readiness,
        ISecurityDialogs dialogs)
    {
        _pinService = pinService;
        _developerOptions = developerOptions;
        _readiness = readiness;
        _dialogs = dialogs;

        RescanCommand = new RelayCommand(() => _ = RefreshAsync());
        ShowComparisonCommand = new RelayCommand(() => _ = _dialogs.ShowModeComparisonAsync());
        ShowPlanCommand = new RelayCommand(() => _ = ShowPlanAsync());

        Refresh();
    }

    public ObservableCollection<SecurityStatusViewModel> Statuses { get; } = [];

    public ObservableCollection<SecurityFactViewModel> WindowsFacts { get; } = [];

    public ObservableCollection<SecurityStatusViewModel> Capabilities { get; } = [];

    public ObservableCollection<SecurityCheckViewModel> Checks { get; } = [];

    public ObservableCollection<string> Blockers { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<SecurityFactViewModel> Diagnostics { get; } = [];

    public RelayCommand RescanCommand { get; }

    public RelayCommand ShowComparisonCommand { get; }

    public RelayCommand ShowPlanCommand { get; }

    public string ConfigurationPath => AppPaths.ConfigurationFilePath;

    public string LogPath => AppPaths.LogFilePath;

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    // ----------------------------------------------------------- headline

    /// <summary>
    /// The state actually in force. Never says "Skyddad": that word is only
    /// earned once Windows restrictions are applied and verified, which no
    /// 0.1.x build can do.
    /// </summary>
    public string StatusHeadline => (_report?.OverallState ?? ReadinessState.DevelopmentOnly) switch
    {
        ReadinessState.Ready => Strings.Get("Security.StateReady"),
        ReadinessState.ReadyWithWarnings => Strings.Get("Security.StateReadyWithWarnings"),
        ReadinessState.NotReady => Strings.Get("Security.StateNotReady"),
        _ => Strings.Get("Security.StateDevelopment")
    };

    public string StatusBody => (_report?.OverallState ?? ReadinessState.DevelopmentOnly) switch
    {
        ReadinessState.Ready => Strings.Get("Security.StateReadyBody"),
        ReadinessState.ReadyWithWarnings => Strings.Get("Security.StateReadyWithWarningsBody"),
        ReadinessState.NotReady => Strings.Get("Security.StateNotReadyBody"),
        _ => Strings.Get("Security.StateDevelopmentBody")
    };

    /// <summary>Shown unconditionally: no 0.1.x build has locked Windows.</summary>
    public string LockNotice => Strings.Get("Security.LockNotEnabled");

    // ----------------------------------------------------------- recommended mode

    public string RecommendedModeTitle => (_report?.RecommendedMode ?? SecurityMode.Development) switch
    {
        SecurityMode.Secure => Strings.Get("Security.ModeSecure"),
        SecurityMode.Standard => Strings.Get("Security.ModeStandard"),
        _ => Strings.Get("Security.ModeDevelopment")
    };

    public string RecommendedModeBody => (_report?.RecommendedMode ?? SecurityMode.Development) switch
    {
        SecurityMode.Secure => Strings.Get("Security.ModeSecureBody"),
        SecurityMode.Standard => Strings.Get("Security.ModeStandardBody"),
        _ => Strings.Get("Security.ModeDevelopmentBody")
    };

    public bool HasBlockers => Blockers.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The summary the Översikt page shows.</summary>
    public string OverviewSummary => (_report?.OverallState ?? ReadinessState.DevelopmentOnly) switch
    {
        ReadinessState.Ready => Strings.Get("Overview.SecurityReady"),
        ReadinessState.ReadyWithWarnings => Strings.Get("Overview.SecurityReadyWithWarnings"),
        ReadinessState.NotReady => Strings.Get("Overview.SecurityNotReady"),
        _ => Strings.Get("Overview.SecurityDevelopment")
    };

    // ----------------------------------------------------------- refresh

    /// <summary>Rebuilds from the last report without re-reading the machine.</summary>
    public void Refresh()
    {
        BuildStatuses();
        BuildWindowsFacts();
        BuildCapabilities();
        BuildChecks();
        BuildDiagnostics();
        NotifyAll();
    }

    /// <summary>Runs a fresh read-only scan and rebuilds the page.</summary>
    public async Task RefreshAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;

        try
        {
            _report = await _readiness.ScanAsync().ConfigureAwait(true);
        }
        finally
        {
            IsScanning = false;
        }

        Refresh();
    }

    private async Task ShowPlanAsync()
    {
        if (_report is null)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        if (_report is not null)
        {
            await _dialogs.ShowSecurityPlanAsync(_report).ConfigureAwait(true);
        }
    }

    // ----------------------------------------------------------- builders

    private void BuildStatuses()
    {
        Statuses.Clear();

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.WindowsLock"),
            Strings.Get("Security.NotEnabled"),
            SecurityState.Inactive));

        var childAccount = _report?.CandidateChildAccounts.FirstOrDefault();
        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.ChildAccount"),
            Strings.Get("Security.NotConfigured"),
            SecurityState.Inactive,
            childAccount is null
                ? Strings.Get("Security.CapChildAccountNone")
                : Strings.Format("Security.CapChildAccountFound", childAccount.Username)));

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.Allowlist"),
            Strings.Get("Security.NotEnabled"),
            SecurityState.Inactive));

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.Watchdog"),
            Strings.Get("Security.NotInstalled"),
            SecurityState.Inactive,
            Strings.Get("Security.CapWatchdogNone")));

        var pinConfigured = _pinService.IsCustomPinConfigured;
        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.ParentPin"),
            pinConfigured
                ? Strings.Get("Security.Active")
                : Strings.Get("Security.DevelopmentPinActive"),
            pinConfigured ? SecurityState.Active : SecurityState.Warning,
            pinConfigured || !_developerOptions.DeveloperMode
                ? null
                : Strings.Get("Pin.DeveloperHint")));
    }

    private void BuildWindowsFacts()
    {
        WindowsFacts.Clear();

        var capabilities = _report?.Capabilities;

        WindowsFacts.Add(new SecurityFactViewModel(
            Strings.Get("Security.EditionLabel"),
            capabilities?.EditionDisplayName ?? Strings.Get("Security.CapUnknown")));

        WindowsFacts.Add(new SecurityFactViewModel(
            Strings.Get("Security.BuildLabel"),
            capabilities is null || capabilities.BuildNumber == 0
                ? Strings.Get("Security.CapUnknown")
                : $"{capabilities.BuildNumber}.{capabilities.UpdateBuildRevision}"));

        WindowsFacts.Add(new SecurityFactViewModel(
            Strings.Get("Security.AccountTypeLabel"),
            capabilities is null
                ? Strings.Get("Security.CapUnknown")
                : capabilities.CurrentUserIsAdministrator
                    ? Strings.Get("Security.AccountAdministrator")
                    : Strings.Get("Security.AccountStandardUser")));

        WindowsFacts.Add(new SecurityFactViewModel(
            Strings.Get("Security.UacLabel"),
            capabilities?.IsUacEnabled switch
            {
                true => Strings.Get("Security.UacActive"),
                false => Strings.Get("Security.UacInactive"),
                null => Strings.Get("Security.UacUnknown")
            }));
    }

    private void BuildCapabilities()
    {
        Capabilities.Clear();

        var capabilities = _report?.Capabilities;

        Capabilities.Add(Capability(
            Strings.Get("Security.CapAssignedAccess"),
            capabilities?.AssignedAccess ?? CapabilityState.Unknown,
            Strings.Get("Security.CapAssignedAccessAvailable"),
            Strings.Get("Security.CapAssignedAccessUnavailable")));

        Capabilities.Add(Capability(
            Strings.Get("Security.CapAppControl"),
            capabilities?.AppLocker ?? CapabilityState.Unknown,
            Strings.Get("Security.CapAppControlAvailable"),
            Strings.Get("Security.CapAppControlUnavailable")));

        // KidShell's own allowlist is genuinely available everywhere, and is
        // labelled so it cannot be mistaken for a Windows guarantee.
        Capabilities.Add(new SecurityStatusViewModel(
            Strings.Get("Security.CapKidShellAllowlist"),
            Strings.Get("Security.CapAvailable"),
            SecurityState.Active,
            Strings.Get("Security.CapKidShellAllowlistHint")));

        var childAccount = _report?.CandidateChildAccounts.FirstOrDefault();
        Capabilities.Add(new SecurityStatusViewModel(
            Strings.Get("Security.CapChildAccount"),
            Strings.Get("Security.NotConfiguredShort"),
            SecurityState.Inactive,
            childAccount is null
                ? Strings.Get("Security.CapChildAccountNone")
                : Strings.Format("Security.CapChildAccountFound", childAccount.Username)));

        Capabilities.Add(new SecurityStatusViewModel(
            Strings.Get("Security.CapWatchdog"),
            Strings.Get("Security.NotInstalledShort"),
            SecurityState.Inactive,
            Strings.Get("Security.CapWatchdogNone")));

        Capabilities.Add(new SecurityStatusViewModel(
            Strings.Get("Security.CapWebPolicy"),
            Strings.Get("Security.NotEnabledShort"),
            SecurityState.Inactive,
            Strings.Get("Security.CapWebPolicyNone")));

        static SecurityStatusViewModel Capability(
            string label,
            CapabilityState state,
            string availableHint,
            string unavailableHint) => state switch
        {
            CapabilityState.Available => new SecurityStatusViewModel(
                label, Strings.Get("Security.CapAvailable"), SecurityState.Active, availableHint),
            CapabilityState.Unavailable => new SecurityStatusViewModel(
                label, Strings.Get("Security.CapUnavailable"), SecurityState.Inactive, unavailableHint),
            _ => new SecurityStatusViewModel(
                label, Strings.Get("Security.CapUnknown"), SecurityState.Warning, null)
        };
    }

    private void BuildChecks()
    {
        Checks.Clear();
        Blockers.Clear();
        Warnings.Clear();

        if (_report is null)
        {
            return;
        }

        foreach (var check in _report.Checks)
        {
            Checks.Add(new SecurityCheckViewModel(check));
        }

        foreach (var blocker in _report.Blockers)
        {
            Blockers.Add(blocker);
        }

        foreach (var warning in _report.Warnings)
        {
            Warnings.Add(warning);
        }
    }

    private void BuildDiagnostics()
    {
        Diagnostics.Clear();

        var capabilities = _report?.Capabilities;
        var unknown = Strings.Get("Security.CapUnknown");

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagEdition"),
            capabilities is null ? unknown : WindowsEditionMap.EditionName(capabilities.Edition)));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagVersion"),
            string.IsNullOrWhiteSpace(capabilities?.Version) ? unknown : capabilities.Version));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagBuild"),
            capabilities is null || capabilities.BuildNumber == 0
                ? unknown
                : $"{capabilities.BuildNumber}.{capabilities.UpdateBuildRevision}"));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagAssignedAccess"),
            DescribeCapability(capabilities?.AssignedAccess)));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagAppControl"),
            DescribeCapability(capabilities?.AppLocker)));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagUac"),
            capabilities?.IsUacEnabled switch
            {
                true => Strings.Get("Security.UacActive"),
                false => Strings.Get("Security.UacInactive"),
                null => unknown
            }));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagUserType"),
            capabilities is null
                ? unknown
                : capabilities.CurrentUserIsAdministrator
                    ? Strings.Get("Security.AccountAdministrator")
                    : Strings.Get("Security.AccountStandardUser")));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagElevated"),
            capabilities is null
                ? unknown
                : capabilities.IsProcessElevated ? Strings.Get("Security.DiagYes") : Strings.Get("Security.DiagNo")));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagPackaged"),
            capabilities is null
                ? unknown
                : capabilities.HasPackageIdentity ? Strings.Get("Security.DiagYes") : Strings.Get("Security.DiagNo")));

        // Stated on the page rather than assumed: the scan ran in AuditOnly.
        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagExecutionMode"),
            (_report?.ExecutionMode ?? SecurityExecutionMode.AuditOnly).ToString()));

        Diagnostics.Add(new SecurityFactViewModel(
            Strings.Get("Security.DiagLastScan"),
            _report is null
                ? Strings.Get("Security.DiagNeverScanned")
                : _report.ScannedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));

        static string DescribeCapability(CapabilityState? state) => state switch
        {
            CapabilityState.Available => Strings.Get("Security.CapAvailable"),
            CapabilityState.Unavailable => Strings.Get("Security.CapUnavailable"),
            _ => Strings.Get("Security.CapUnknown")
        };
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(StatusHeadline));
        OnPropertyChanged(nameof(StatusBody));
        OnPropertyChanged(nameof(LockNotice));
        OnPropertyChanged(nameof(RecommendedModeTitle));
        OnPropertyChanged(nameof(RecommendedModeBody));
        OnPropertyChanged(nameof(HasBlockers));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(OverviewSummary));
    }
}
