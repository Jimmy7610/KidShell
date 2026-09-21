using KidShell.Core.Configuration;
using KidShell.Core.Security;
using KidShell.Core.Security.Readiness;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The readiness scan: pre-flight checks, blockers, plan generation, and the
/// guarantee that none of it changes anything.
/// </summary>
public class SecurityReadinessTests
{
    private static (SecurityReadinessService Service, AppStateService State, RecordingLogger Logger) Create(
        TempDirectory dir,
        WindowsSystemFacts facts,
        bool developerMode = false,
        KidShellConfiguration? config = null,
        params WindowsAccount[] accounts)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        if (config is not null)
        {
            state.Commit(config);
        }

        var service = new SecurityReadinessService(
            new FakeSystemFactsProvider(facts),
            new FakeAccountDiscovery(accounts),
            state,
            new FakeDeveloperOptions(developerMode),
            logger);

        return (service, state, logger);
    }

    // ---------------------------------------------------------- Ready

    [Fact]
    public async Task A_fully_prepared_Pro_machine_is_Ready()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro(),
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        Assert.Equal(ReadinessState.Ready, report.OverallState);
        Assert.Equal(SecurityMode.Secure, report.RecommendedMode);
        Assert.Empty(report.Blockers);
        Assert.Empty(report.Warnings);
    }

    // ---------------------------------------------------------- ReadyWithWarnings

    [Fact]
    public async Task A_Home_machine_is_ReadyWithWarnings_and_recommends_Standard()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Home(),
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        // Home cannot do Secure, but nothing stops Standard, so the missing
        // Assigned Access is a warning rather than a blocker.
        Assert.Equal(ReadinessState.ReadyWithWarnings, report.OverallState);
        Assert.Equal(SecurityMode.Standard, report.RecommendedMode);
        Assert.Empty(report.Blockers);
        Assert.Contains(report.Warnings, w => w.Contains("Assigned Access", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------- NotReady

    [Fact]
    public async Task No_recovery_administrator_makes_the_machine_NotReady()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro(),
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Standard("Barn"),
            SecurityFixtures.BuiltIn());

        var report = await service.ScanAsync();

        Assert.Equal(ReadinessState.NotReady, report.OverallState);
        Assert.True(report.HasBlockers);
        Assert.Null(report.RecoveryAccount);
    }

    [Fact]
    public async Task A_disabled_administrator_does_not_count_as_recovery()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro(),
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin("Administratör", enabled: false),
            SecurityFixtures.Standard("Barn"));

        var report = await service.ScanAsync();

        Assert.Null(report.RecoveryAccount);
        Assert.Equal(ReadinessState.NotReady, report.OverallState);
    }

    [Fact]
    public async Task Disabled_uac_is_a_blocker()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro() with { IsUacEnabled = false },
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        Assert.Equal(ReadinessState.NotReady, report.OverallState);
        Assert.Contains(report.Blockers, b => b.Contains("UAC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blockers_are_written_in_plain_language_not_error_codes()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro() with { IsUacEnabled = false, CurrentUserSid = "S-1-5-21-1-2-3-1500" },
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        Assert.NotEmpty(report.Blockers);
        Assert.All(report.Blockers, b =>
        {
            Assert.DoesNotContain("HRESULT", b, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("0x", b, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", b, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---------------------------------------------------------- DevelopmentOnly

    [Fact]
    public async Task A_developer_build_always_reports_DevelopmentOnly()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro(),
            developerMode: true,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        // Even on a perfectly capable machine: saying "Ready" while the child
        // is completely unrestricted would be the most misleading thing this
        // screen could do.
        Assert.Equal(ReadinessState.DevelopmentOnly, report.OverallState);
        Assert.Equal(SecurityMode.Development, report.CurrentMode);
    }

    [Fact]
    public async Task The_current_mode_is_never_Secure_in_this_milestone()
    {
        using var dir = new TempDirectory();

        foreach (var facts in new[]
                 {
                     SecurityFixtures.Windows11Home(),
                     SecurityFixtures.Windows11Pro(),
                     SecurityFixtures.Windows11Enterprise()
                 })
        {
            var (service, _, _) = Create(dir, facts, developerMode: false,
                SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

            var report = await service.ScanAsync();

            Assert.Equal(SecurityMode.Development, report.CurrentMode);
            Assert.False(report.WindowsLockdownEnabled);
        }
    }

    // ---------------------------------------------------------- pre-flight

    [Fact]
    public async Task Incomplete_onboarding_is_a_preflight_blocker()
    {
        using var dir = new TempDirectory();

        // Default configuration has no child profile yet.
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro(),
            developerMode: false,
            config: null,
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        var check = Assert.Single(report.Checks, c => c.Id == "onboarding");
        Assert.Equal(CheckStatus.Failed, check.Status);
        Assert.True(check.BlocksRecommendedMode);
        Assert.Equal(ReadinessState.NotReady, report.OverallState);
    }

    [Fact]
    public async Task Completed_onboarding_passes_the_preflight_check()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir,
            SecurityFixtures.Windows11Pro(),
            developerMode: false,
            SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        Assert.True(Assert.Single(report.Checks, c => c.Id == "onboarding").IsPassed);
        Assert.True(Assert.Single(report.Checks, c => c.Id == "child-name").IsPassed);
    }

    [Fact]
    public async Task At_least_one_enabled_app_is_required()
    {
        using var dir = new TempDirectory();

        var config = SecurityFixtures.ConfiguredChild();
        foreach (var app in config.Apps)
        {
            app.IsEnabled = false;
        }

        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false, config, SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        var check = Assert.Single(report.Checks, c => c.Id == "enabled-apps");
        Assert.Equal(CheckStatus.Failed, check.Status);
        Assert.Equal(ReadinessState.NotReady, report.OverallState);
    }

    [Fact]
    public async Task Every_expected_preflight_check_is_present()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();
        var ids = report.Checks.Select(c => c.Id).ToArray();

        Assert.Contains("recovery-admin", ids);
        Assert.Contains("uac", ids);
        Assert.Contains("assigned-access", ids);
        Assert.Contains("current-user-admin", ids);
        Assert.Contains("onboarding", ids);
        Assert.Contains("child-name", ids);
        Assert.Contains("enabled-apps", ids);
        Assert.Contains("package-identity", ids);
        Assert.Contains("config-writable", ids);
    }

    [Fact]
    public async Task Missing_assigned_access_is_a_warning_not_a_blocker_on_Home()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Home(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        var check = Assert.Single(report.Checks, c => c.Id == "assigned-access");
        Assert.Equal(CheckStatus.Warning, check.Status);
        Assert.False(check.BlocksRecommendedMode);
    }

    // ---------------------------------------------------------- accounts

    [Fact]
    public async Task Discovered_accounts_are_classified()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false, SecurityFixtures.ConfiguredChild(),
            SecurityFixtures.Admin("Jimmy"),
            SecurityFixtures.Standard("Barn"),
            SecurityFixtures.BuiltIn("DefaultAccount"),
            SecurityFixtures.Standard("Gammal", enabled: false));

        var report = await service.ScanAsync();

        Assert.Equal(4, report.DiscoveredAccounts.Count);
        Assert.Equal("Jimmy", report.RecoveryAccount?.Username);

        var candidates = report.CandidateChildAccounts;
        Assert.Single(candidates);
        Assert.Equal("Barn", candidates[0].Username);
    }

    [Fact]
    public async Task Unreadable_accounts_degrade_the_report_rather_than_throwing()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();
        state.Commit(SecurityFixtures.ConfiguredChild());

        var service = new SecurityReadinessService(
            new FakeSystemFactsProvider(SecurityFixtures.Windows11Pro()),
            new FakeAccountDiscovery(new InvalidOperationException("netapi32 unavailable")),
            state,
            new FakeDeveloperOptions(false),
            logger);

        var report = await service.ScanAsync();

        Assert.Empty(report.DiscoveredAccounts);
        Assert.Equal(CheckStatus.NotApplicable, Assert.Single(report.Checks, c => c.Id == "recovery-admin").Status);
        Assert.True(logger.HasError);
    }

    [Fact]
    public async Task Unreadable_system_facts_degrade_the_report_rather_than_throwing()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var service = new SecurityReadinessService(
            new FakeSystemFactsProvider(new InvalidOperationException("registry unavailable")),
            new FakeAccountDiscovery(),
            state,
            new FakeDeveloperOptions(false),
            logger);

        var report = await service.ScanAsync();

        Assert.True(report.Capabilities.DetectionFailed);
        Assert.False(report.Capabilities.SupportsSecureMode);
        Assert.True(logger.HasError);
    }

    // ---------------------------------------------------------- plan

    [Fact]
    public async Task A_Secure_machine_gets_the_full_plan()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();
        var ids = report.PlannedActions.Select(a => a.Id).ToArray();

        Assert.Contains("child-account", ids);
        Assert.Contains("verify-standard-user", ids);
        Assert.Contains("verify-recovery-account", ids);
        Assert.Contains("configure-apps", ids);
        Assert.Contains("configure-assigned-access", ids);
        Assert.Contains("configure-autostart", ids);
        Assert.Contains("configure-browser-policy", ids);
        Assert.Contains("install-watchdog", ids);
    }

    [Fact]
    public async Task A_Standard_machine_plan_omits_the_assigned_access_step()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Home(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();
        var ids = report.PlannedActions.Select(a => a.Id).ToArray();

        Assert.DoesNotContain("configure-assigned-access", ids);
        Assert.Contains("child-account", ids);
        Assert.Contains("configure-apps", ids);
    }

    [Fact]
    public async Task A_Home_plan_still_includes_app_control()
    {
        // Home enforces AppLocker, so dropping the app-control step just
        // because Assigned Access is missing would hide a protection the
        // machine genuinely supports. The step is planned; its detail says
        // the deployment route still has to be established.
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Home(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        var step = Assert.Single(report.PlannedActions, a => a.Id == "configure-applocker");
        Assert.Equal(RequiredCapability.AppLockerEnforcement, step.CapabilityRequired);
        Assert.Contains("saknar", step.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(step.WasExecuted);
    }

    [Fact]
    public void A_machine_with_a_deployment_channel_gets_the_plain_app_control_step()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());
        var plan = SecurityPlanBuilder.Build(capabilities, SecurityMode.Secure);

        var step = Assert.Single(plan, a => a.Id == "configure-applocker");
        Assert.DoesNotContain("saknar", step.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_machine_that_cannot_enforce_gets_no_app_control_step()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { AppIdentityServicePresent = false });

        var plan = SecurityPlanBuilder.Build(capabilities, SecurityMode.Standard);

        Assert.DoesNotContain(plan, a => a.Id == "configure-applocker");
    }

    [Fact]
    public void Planned_actions_are_numbered_and_fully_described()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Enterprise());
        var plan = SecurityPlanBuilder.Build(capabilities, SecurityMode.Secure);

        Assert.NotEmpty(plan);
        Assert.Equal(Enumerable.Range(1, plan.Count), plan.Select(a => a.Order));

        Assert.All(plan, action =>
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Id));
            Assert.False(string.IsNullOrWhiteSpace(action.Description));
            Assert.False(string.IsNullOrWhiteSpace(action.Detail));
            Assert.True(Enum.IsDefined(action.RiskLevel));
            Assert.True(Enum.IsDefined(action.CapabilityRequired));
        });
    }

    [Fact]
    public void An_enterprise_plan_includes_the_applocker_step()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Enterprise());
        var plan = SecurityPlanBuilder.Build(capabilities, SecurityMode.Secure);

        var step = Assert.Single(plan, a => a.Id == "configure-applocker");
        Assert.Equal(RequiredCapability.AppLockerEnforcement, step.CapabilityRequired);
        Assert.True(step.RequiresAdmin);
    }

    [Fact]
    public void Development_mode_produces_no_plan_at_all()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());

        Assert.Empty(SecurityPlanBuilder.Build(capabilities, SecurityMode.Development));
    }

    [Fact]
    public async Task No_planned_action_is_ever_marked_executed()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        Assert.NotEmpty(report.PlannedActions);
        Assert.All(report.PlannedActions, a => Assert.False(a.WasExecuted));
    }

    // ---------------------------------------------------------- audit-only

    [Fact]
    public async Task Every_scan_runs_in_AuditOnly()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var report = await service.ScanAsync();

        Assert.Equal(SecurityExecutionMode.AuditOnly, report.ExecutionMode);
    }

    [Fact]
    public async Task Scanning_does_not_change_KidShell_configuration()
    {
        using var dir = new TempDirectory();
        var config = SecurityFixtures.ConfiguredChild();
        var (service, state, _) = Create(
            dir, SecurityFixtures.Windows11Pro(), developerMode: false, config, SecurityFixtures.Admin());

        var before = state.Current.Clone();
        var bytesBefore = File.ReadAllBytes(dir.ConfigPath);

        await service.ScanAsync();
        await service.ScanAsync();
        await service.ScanAsync();

        Assert.True(ConfigurationSnapshot.AreEquivalent(before, state.Current));
        Assert.Equal(bytesBefore, File.ReadAllBytes(dir.ConfigPath));
    }

    [Fact]
    public async Task Repeated_scans_are_stable()
    {
        using var dir = new TempDirectory();
        var (service, _, _) = Create(
            dir, SecurityFixtures.Windows11Home(), developerMode: false,
            SecurityFixtures.ConfiguredChild(), SecurityFixtures.Admin());

        var first = await service.ScanAsync();
        var second = await service.ScanAsync();

        Assert.Equal(first.OverallState, second.OverallState);
        Assert.Equal(first.RecommendedMode, second.RecommendedMode);
        Assert.Equal(first.Blockers, second.Blockers);
        Assert.Equal(first.PlannedActions.Count, second.PlannedActions.Count);
        Assert.Same(second, service.LastReport);
    }

    [Fact]
    public async Task The_scan_writes_named_audit_events_without_secrets()
    {
        using var dir = new TempDirectory();
        var config = SecurityFixtures.ConfiguredChild();
        config.ParentPin.Hash = "super-secret-hash";
        config.ParentPin.Salt = "super-secret-salt";

        var (service, _, logger) = Create(
            dir, SecurityFixtures.Windows11Home(), developerMode: false, config, SecurityFixtures.Admin());

        await service.ScanAsync();

        var log = string.Join("\n", logger.Messages);

        Assert.Contains(SecurityAuditEvents.CapabilityDetected, log, StringComparison.Ordinal);
        Assert.Contains(SecurityAuditEvents.PreflightRun, log, StringComparison.Ordinal);
        Assert.Contains(SecurityAuditEvents.PlanGenerated, log, StringComparison.Ordinal);

        Assert.DoesNotContain("super-secret-hash", log, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-salt", log, StringComparison.Ordinal);
        Assert.DoesNotContain(DevelopmentPin.Value, log, StringComparison.Ordinal);
    }
}
