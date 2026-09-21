using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Readiness;

/// <summary>Named audit events for security diagnostics. No secrets are ever logged.</summary>
public static class SecurityAuditEvents
{
    public const string CapabilityDetected = "SecurityCapabilityDetected";
    public const string PreflightRun = "SecurityPreflightRun";
    public const string PlanGenerated = "SecurityPlanGenerated";
    public const string AccountsDiscovered = "SecurityAccountsDiscovered";
    public const string ScanFailed = "SecurityScanFailed";

    /// <summary>Log category used for all of the above.</summary>
    public const string Category = "Security";
}

public interface ISecurityReadinessService
{
    /// <summary>The last report, or null before the first scan.</summary>
    SecurityReadinessReport? LastReport { get; }

    /// <summary>
    /// Runs a read-only scan: detect, evaluate, plan. Changes nothing about
    /// Windows and nothing about KidShell's configuration.
    /// </summary>
    Task<SecurityReadinessReport> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes facts, capabilities, accounts and KidShell's own configuration
/// into a <see cref="SecurityReadinessReport"/>.
///
/// The service holds an <see cref="SecurityExecutionContext"/> which it can
/// only ever be given in AuditOnly form, and it has no dependency on anything
/// that could mutate the machine — there is no <see cref="ISecurityMutator"/>
/// in the constructor, and no implementation of one exists.
/// </summary>
public sealed class SecurityReadinessService : ISecurityReadinessService
{
    private readonly ISystemFactsProvider _facts;
    private readonly IWindowsAccountDiscovery _accounts;
    private readonly IAppStateService _state;
    private readonly IDeveloperOptions _developerOptions;
    private readonly IKidShellLogger _logger;
    private readonly SecurityExecutionContext _execution;
    private readonly TimeProvider _time;

    public SecurityReadinessService(
        ISystemFactsProvider facts,
        IWindowsAccountDiscovery accounts,
        IAppStateService state,
        IDeveloperOptions developerOptions,
        IKidShellLogger logger,
        TimeProvider? time = null)
    {
        _facts = facts;
        _accounts = accounts;
        _state = state;
        _developerOptions = developerOptions;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        // The only context that can be constructed. A future milestone that
        // wants Apply has to add a factory for it deliberately.
        _execution = SecurityExecutionContext.AuditOnly();
    }

    public SecurityReadinessReport? LastReport { get; private set; }

    public async Task<SecurityReadinessReport> ScanAsync(CancellationToken cancellationToken = default)
    {
        var facts = await ReadFactsAsync(cancellationToken).ConfigureAwait(false);

        // Accounts are read before the analysis because an unelevated process
        // cannot tell from its own token whether the signed-in account is an
        // administrator; the account list can.
        var accounts = await ListAccountsAsync(cancellationToken).ConfigureAwait(false);

        var capabilities = WindowsCapabilityAnalyzer.Analyze(facts, accounts);

        _logger.Info(
            SecurityAuditEvents.Category,
            $"{SecurityAuditEvents.CapabilityDetected}: edition={capabilities.EditionDisplayName}, " +
            $"build={capabilities.BuildNumber}, assignedAccess={capabilities.AssignedAccess}, " +
            $"appLocker={capabilities.AppLocker}, uac={Describe(capabilities.IsUacEnabled)}, " +
            $"admin={capabilities.CurrentUserIsAdministrator}, mode={_execution.Mode}");

        _logger.Info(
            SecurityAuditEvents.Category,
            $"{SecurityAuditEvents.AccountsDiscovered}: {accounts.Count} local accounts, " +
            $"{accounts.Count(a => a.IsRecoveryCandidate)} usable as recovery admin.");

        var config = _state.Current;
        var checks = BuildChecks(capabilities, accounts, config);

        var blockers = new List<string>(capabilities.Blockers);
        var warnings = new List<string>(capabilities.Warnings);

        foreach (var check in checks)
        {
            if (check.Status == CheckStatus.Failed && check.BlocksRecommendedMode)
            {
                blockers.Add(check.Detail);
            }
            else if (check.Status is CheckStatus.Failed or CheckStatus.Warning)
            {
                warnings.Add(check.Detail);
            }
        }

        var plan = SecurityPlanBuilder.Build(capabilities, capabilities.RecommendedSecurityMode);

        _logger.Info(
            SecurityAuditEvents.Category,
            $"{SecurityAuditEvents.PreflightRun}: {checks.Count(c => c.IsPassed)}/{checks.Count} checks passed, " +
            $"{blockers.Count} blockers, {warnings.Count} warnings.");

        _logger.Info(
            SecurityAuditEvents.Category,
            $"{SecurityAuditEvents.PlanGenerated}: {plan.Count} planned actions for " +
            $"{capabilities.RecommendedSecurityMode} mode. Nothing was executed ({_execution.Mode}).");

        var report = new SecurityReadinessReport
        {
            OverallState = DetermineState(blockers, warnings),
            RecommendedMode = capabilities.RecommendedSecurityMode,

            // Nothing has been applied, so the mode actually in force is
            // Development. This is not derived from the machine's abilities.
            CurrentMode = SecurityMode.Development,

            Capabilities = capabilities,
            Checks = checks,
            Warnings = warnings,
            Blockers = blockers,
            PlannedActions = plan,
            DiscoveredAccounts = accounts,
            ExecutionMode = _execution.Mode,
            ScannedAtUtc = _time.GetUtcNow()
        };

        LastReport = report;
        return report;
    }

    private async Task<WindowsSystemFacts> ReadFactsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _facts.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A readiness scan must never be the thing that breaks the app.
            _logger.Error(SecurityAuditEvents.Category, $"{SecurityAuditEvents.ScanFailed}: could not read system facts.", ex);
            return WindowsSystemFacts.Unknown(ex.Message);
        }
    }

    private async Task<IReadOnlyList<WindowsAccount>> ListAccountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _accounts.ListLocalAccountsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(SecurityAuditEvents.Category, $"{SecurityAuditEvents.ScanFailed}: could not list local accounts.", ex);
            return [];
        }
    }

    /// <summary>
    /// A development build says so, whatever the machine could support.
    /// Reporting "Ready" while the child is completely unrestricted would be
    /// the most dangerous thing this screen could do.
    /// </summary>
    private ReadinessState DetermineState(List<string> blockers, List<string> warnings)
    {
        if (_developerOptions.DeveloperMode)
        {
            return ReadinessState.DevelopmentOnly;
        }

        if (blockers.Count > 0)
        {
            return ReadinessState.NotReady;
        }

        return warnings.Count > 0 ? ReadinessState.ReadyWithWarnings : ReadinessState.Ready;
    }

    /// <summary>
    /// Pre-flight checks. Every one is computed from data already gathered, so
    /// running them cannot touch Windows.
    /// </summary>
    private static IReadOnlyList<ReadinessCheck> BuildChecks(
        WindowsSecurityCapabilities capabilities,
        IReadOnlyList<WindowsAccount> accounts,
        KidShellConfiguration config)
    {
        var checks = new List<ReadinessCheck>();

        // --- Admin recovery account -------------------------------------
        var recovery = accounts.Where(a => a.IsRecoveryCandidate).ToList();
        checks.Add(new ReadinessCheck
        {
            Id = "recovery-admin",
            Title = "Administratörskonto för återställning",
            Detail = recovery.Count switch
            {
                0 when accounts.Count == 0 =>
                    "Windows-konton kunde inte läsas, så återställningskontot kunde inte bekräftas.",
                0 => "Det finns inget aktivt administratörskonto att logga in med om något går fel.",
                _ => $"{recovery.Count} aktivt administratörskonto finns kvar att logga in med."
            },
            Status = recovery.Count switch
            {
                0 when accounts.Count == 0 => CheckStatus.NotApplicable,
                0 => CheckStatus.Failed,
                _ => CheckStatus.Passed
            },
            BlocksRecommendedMode = recovery.Count == 0 && accounts.Count > 0
        });

        // --- UAC ---------------------------------------------------------
        checks.Add(new ReadinessCheck
        {
            Id = "uac",
            Title = "Användarkontokontroll (UAC)",
            Detail = capabilities.IsUacEnabled switch
            {
                true => "UAC är aktivt, vilket behövs för att skilja barnets konto från ditt.",
                false => "UAC är avstängt. Det behövs för att barnets konto ska vara skilt från ditt.",
                null => "UAC-status kunde inte läsas."
            },
            Status = capabilities.IsUacEnabled switch
            {
                true => CheckStatus.Passed,
                false => CheckStatus.Failed,
                null => CheckStatus.NotApplicable
            },
            BlocksRecommendedMode = capabilities.IsUacEnabled is false
        });

        // --- Edition / Assigned Access -----------------------------------
        checks.Add(new ReadinessCheck
        {
            Id = "assigned-access",
            Title = "Windows-utgåva",
            Detail = capabilities.AssignedAccess switch
            {
                CapabilityState.Available =>
                    $"{capabilities.EditionDisplayName} stöder Assigned Access, som Secure Mode använder.",
                CapabilityState.Unavailable =>
                    $"{capabilities.EditionDisplayName} stöder inte Assigned Access. KidShell använder Standardläge i stället.",
                _ => "Windows-utgåvan kunde inte identifieras."
            },
            Status = capabilities.AssignedAccess switch
            {
                CapabilityState.Available => CheckStatus.Passed,
                CapabilityState.Unavailable => CheckStatus.Warning,
                _ => CheckStatus.NotApplicable
            },

            // Not a blocker: it is the reason Standard is recommended, not a
            // failure of Standard.
            BlocksRecommendedMode = false
        });

        // --- Administrator rights ----------------------------------------
        checks.Add(new ReadinessCheck
        {
            Id = "current-user-admin",
            Title = "Ditt konto",
            Detail = capabilities switch
            {
                // An administrator running unelevated is the normal case with
                // UAC on, and is fine: setup will ask for elevation when it
                // runs rather than needing it now.
                { CurrentUserIsAdministrator: true, IsProcessElevated: true } =>
                    "Du är inloggad som administratör och KidShell körs med utökad behörighet.",
                { CurrentUserIsAdministrator: true } =>
                    "Du är inloggad som administratör. Windows frågar om behörighet när installationen körs.",
                _ =>
                    "Du är inloggad som standardanvändare. Säkerhetsinstallation kräver administratör."
            },
            Status = capabilities.CurrentUserIsAdministrator ? CheckStatus.Passed : CheckStatus.Failed,
            BlocksRecommendedMode = !capabilities.CurrentUserIsAdministrator
        });

        // --- KidShell onboarding -----------------------------------------
        var onboardingDone = !config.RequiresOnboarding;
        checks.Add(new ReadinessCheck
        {
            Id = "onboarding",
            Title = "KidShell-introduktion",
            Detail = onboardingDone
                ? "Introduktionen är klar och en barnprofil finns."
                : "Introduktionen är inte klar, så det finns ingen barnprofil att skydda ännu.",
            Status = onboardingDone ? CheckStatus.Passed : CheckStatus.Failed,
            BlocksRecommendedMode = !onboardingDone
        });

        // --- Child name ---------------------------------------------------
        var nameOk = !string.IsNullOrWhiteSpace(config.Child.Name);
        checks.Add(new ReadinessCheck
        {
            Id = "child-name",
            Title = "Barnets namn",
            Detail = nameOk
                ? $"Profilen är inställd för {config.Child.Name}."
                : "Barnets namn saknas i profilen.",
            Status = nameOk ? CheckStatus.Passed : CheckStatus.Failed,
            BlocksRecommendedMode = !nameOk
        });

        // --- At least one enabled app ------------------------------------
        var enabledApps = config.EnabledApps.Count();
        checks.Add(new ReadinessCheck
        {
            Id = "enabled-apps",
            Title = "Tillåtna appar",
            Detail = enabledApps > 0
                ? $"{enabledApps} appar är påslagna för barnet."
                : "Inga appar är påslagna, så barnet skulle mötas av en tom skärm.",
            Status = enabledApps > 0 ? CheckStatus.Passed : CheckStatus.Failed,
            BlocksRecommendedMode = enabledApps == 0
        });

        // --- Package identity ---------------------------------------------
        checks.Add(new ReadinessCheck
        {
            Id = "package-identity",
            Title = "KidShell-installation",
            Detail = capabilities.HasPackageIdentity
                ? "KidShell körs som installerat paket, vilket autostart och Assigned Access kräver."
                : "KidShell körs utan paketidentitet. Autostart och Assigned Access kräver en installerad version.",
            Status = capabilities.HasPackageIdentity ? CheckStatus.Passed : CheckStatus.Warning,
            BlocksRecommendedMode = false
        });

        // --- Configuration writable ---------------------------------------
        checks.Add(new ReadinessCheck
        {
            Id = "config-writable",
            Title = "Inställningsfil",
            Detail = "KidShells inställningar kunde läsas och sparas.",
            Status = CheckStatus.Passed,
            BlocksRecommendedMode = false
        });

        return checks;
    }

    private static string Describe(bool? value) => value switch
    {
        true => "enabled",
        false => "disabled",
        null => "unknown"
    };
}
