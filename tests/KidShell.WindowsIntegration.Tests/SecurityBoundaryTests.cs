using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.Core.Web;
using KidShell.Core.Configuration;
using KidShell.WindowsIntegration.Broker;
using KidShell.WindowsIntegration.Operations;
using KidShell.WindowsIntegration.Platform;
using Xunit;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>
/// What the elevated helper accepts, and — more importantly — what it refuses.
///
/// The helper runs as an administrator, so its input validation is a security
/// boundary rather than a convenience. Every test here describes an input that
/// must never reach an operation.
/// </summary>
public class ElevatedRequestValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("name.")]
    [InlineData("...")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaa")]
    public void Hostile_user_names_are_rejected(string username) =>
        Assert.NotNull(ElevatedRequestValidator.ValidateUserName(username));

    [Theory]
    [InlineData("Lucas")]
    [InlineData("Åsa")]
    [InlineData("barn 1")]
    public void Ordinary_user_names_are_accepted(string username) =>
        Assert.Null(ElevatedRequestValidator.ValidateUserName(username));

    [Theory]
    [InlineData("")]
    [InlineData("administrator")]
    [InlineData("S-1-5-21-..\\..\\SOFTWARE")]
    [InlineData("S-1-5-21-abc")]
    [InlineData("../../etc")]
    [InlineData("S-")]
    public void A_SID_that_is_not_a_SID_is_rejected(string sid) =>
        // This value names a registry hive. Anything but the real grammar is a
        // path-traversal attempt waiting to happen.
        Assert.NotNull(ElevatedRequestValidator.ValidateSid(sid));

    [Fact]
    public void A_real_SID_is_accepted() =>
        Assert.Null(ElevatedRequestValidator.ValidateSid("S-1-5-21-1111111111-2222222222-3333333333-1001"));

    [Theory]
    [InlineData("")]
    [InlineData("no-bang")]
    [InlineData("Pkg_abc!App\"quote")]
    [InlineData("Pkg_abc!App&calc")]
    [InlineData("Pkg_abc!App|more")]
    [InlineData("Pkg_abc!App%COMSPEC%")]
    public void An_AUMID_that_could_change_a_command_is_rejected(string aumid) =>
        // The AUMID is written into a Run value Windows executes.
        Assert.NotNull(ElevatedRequestValidator.ValidateAumid(aumid));

    [Fact]
    public void A_real_AUMID_is_accepted() =>
        Assert.Null(ElevatedRequestValidator.ValidateAumid("KidShell.Barnlage.Dev_8wekyb3d8bbwe!App"));

    [Theory]
    [InlineData("")]
    [InlineData("watchdog.exe")]
    [InlineData(@"..\watchdog.exe")]
    [InlineData(@"C:\KidShell\..\Windows\System32\cmd.exe")]
    [InlineData(@"C:\KidShell\watchdog.dll")]
    public void A_service_path_that_is_not_an_absolute_exe_is_rejected(string path) =>
        Assert.NotNull(ElevatedRequestValidator.ValidateExecutablePath(path));

    [Fact]
    public void A_real_service_path_is_accepted() =>
        Assert.Null(ElevatedRequestValidator.ValidateExecutablePath(@"C:\Program Files\KidShell\KidShell.Watchdog.exe"));

    [Fact]
    public void An_undefined_operation_kind_is_rejected()
    {
        // An enum value outside the closed set means the caller is not the
        // KidShell this helper shipped with.
        var request = new ElevatedRequest { Kind = (ElevatedOperationKind)9999, RequestId = "x" };

        Assert.NotNull(ElevatedRequestValidator.Validate(request));
    }

    [Fact]
    public void A_request_without_an_id_is_rejected() =>
        Assert.NotNull(ElevatedRequestValidator.Validate(
            new ElevatedRequest { Kind = ElevatedOperationKind.Probe, RequestId = "" }));

    [Fact]
    public void An_oversized_artifact_is_rejected()
    {
        var request = new ElevatedRequest
        {
            Kind = ElevatedOperationKind.DeployAppLockerPolicy,
            RequestId = "x",
            PolicyXml = new string('a', 2 * 1024 * 1024)
        };

        // KidShell does not generate a two-megabyte policy, so something else
        // did.
        Assert.NotNull(ElevatedRequestValidator.Validate(request));
    }

    [Fact]
    public void Malformed_JSON_is_rejected_rather_than_guessed_at() =>
        Assert.Null(ElevatedProtocol.DeserializeRequest("{\"kind\": not json"));

    [Fact]
    public void The_request_contract_has_no_command_or_script_field()
    {
        // The structural version of "no arbitrary-command IPC". If a field that
        // carries something to evaluate appears, this fails before it ships.
        //
        // "exec" is deliberately not on this list. ExecutablePath exists, and it
        // is not a command: it is validated to be a fully qualified path ending
        // in .exe with no traversal, and it reaches Windows as a service binary
        // path rather than a command line. The test below pins that validation,
        // which is the property that actually matters.
        var forbidden = new[] { "command", "script", "arguments", "commandline", "powershell", "cmdline" };

        var properties = typeof(ElevatedRequest).GetProperties().Select(p => p.Name.ToLowerInvariant());

        foreach (var property in properties)
        {
            Assert.DoesNotContain(forbidden, f => property.Contains(f, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_only_path_field_must_be_a_validated_absolute_executable()
    {
        // ExecutablePath is the one field naming something Windows will run, so
        // every shape that is not "an absolute path to an .exe" is refused.
        Assert.NotNull(ElevatedRequestValidator.ValidateExecutablePath(@"C:\Windows\System32\cmd.exe /c del"));
        Assert.NotNull(ElevatedRequestValidator.ValidateExecutablePath(@"C:\...exe"));
        Assert.NotNull(ElevatedRequestValidator.ValidateExecutablePath("powershell.exe"));

        Assert.Null(ElevatedRequestValidator.ValidateExecutablePath(
            @"C:\Program Files\KidShell\KidShell.Watchdog.exe"));
    }

    [Fact]
    public void The_request_contract_carries_no_password()
    {
        // A credential must not travel through IPC, reach a log, or land in the
        // recovery manifest.
        var properties = typeof(ElevatedRequest).GetProperties().Select(p => p.Name.ToLowerInvariant());

        foreach (var forbidden in new[] { "password", "secret", "credential", "pin", "token" })
        {
            Assert.DoesNotContain(properties, p => p.Contains(forbidden, StringComparison.Ordinal));
        }
    }
}

/// <summary>The AppLocker policy and Assigned Access configuration guards.</summary>
public class PolicyValidationTests
{
    private const string AdministratorsSid = "S-1-5-32-544";

    private static string PolicyWithAdminEscape() => $"""
        <AppLockerPolicy Version="1">
          <RuleCollection Type="Exe" EnforcementMode="Enabled">
            <FilePathRule Id="11111111-1111-1111-1111-111111111111" Name="Administrators"
                          UserOrGroupSid="{AdministratorsSid}" Action="Allow">
              <Conditions><FilePathCondition Path="*" /></Conditions>
            </FilePathRule>
          </RuleCollection>
        </AppLockerPolicy>
        """;

    private static string PolicyWithoutAdminEscape() => """
        <AppLockerPolicy Version="1">
          <RuleCollection Type="Exe" EnforcementMode="Enabled">
            <FilePathRule Id="22222222-2222-2222-2222-222222222222" Name="Child only"
                          UserOrGroupSid="S-1-5-21-1-2-3-1002" Action="Allow">
              <Conditions><FilePathCondition Path="C:\Games\*" /></Conditions>
            </FilePathRule>
          </RuleCollection>
        </AppLockerPolicy>
        """;

    [Fact]
    public void A_policy_that_could_lock_out_an_administrator_is_refused()
    {
        // The most consequential check in the codebase. A policy with no
        // administrator escape means a failed rollback ends the family's use of
        // the machine.
        var error = AppLockerDeploymentOperation.ValidatePolicy(PolicyWithoutAdminEscape());

        Assert.NotNull(error);
        Assert.Contains("administratörer", error);
    }

    [Fact]
    public void A_policy_with_an_administrator_escape_is_accepted() =>
        Assert.Null(AppLockerDeploymentOperation.ValidatePolicy(PolicyWithAdminEscape()));

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<SomethingElse />")]
    [InlineData("<AppLockerPolicy />")]
    public void A_policy_that_is_not_a_policy_is_refused(string xml) =>
        Assert.NotNull(AppLockerDeploymentOperation.ValidatePolicy(xml));

    [Fact]
    public void An_audit_only_collection_does_not_need_an_escape_rule()
    {
        // Audit mode blocks nothing, so it cannot lock anybody out.
        var audit = $"""
            <AppLockerPolicy Version="1">
              <RuleCollection Type="Exe" EnforcementMode="AuditOnly">
                <FilePathRule Id="33333333-3333-3333-3333-333333333333" Name="Child"
                              UserOrGroupSid="S-1-5-21-1-2-3-1002" Action="Allow">
                  <Conditions><FilePathCondition Path="*" /></Conditions>
                </FilePathRule>
              </RuleCollection>
            </AppLockerPolicy>
            """;

        Assert.Null(AppLockerDeploymentOperation.ValidatePolicy(audit));
    }

    [Fact]
    public void An_empty_export_is_recognised_as_no_policy() =>
        Assert.True(AppLockerDeploymentOperation.IsEmptyPolicy(
            "<AppLockerPolicy Version=\"1\"><RuleCollection Type=\"Exe\" /></AppLockerPolicy>"));

    [Fact]
    public void Enforcement_is_detected_separately_from_presence()
    {
        // Rules present in audit mode protect nobody. Reporting that as
        // protection is the exact overclaim this codebase avoids.
        Assert.True(AppLockerDeploymentOperation.HasEnforcedCollection(PolicyWithAdminEscape()));

        Assert.False(AppLockerDeploymentOperation.HasEnforcedCollection(
            "<AppLockerPolicy><RuleCollection Type=\"Exe\" EnforcementMode=\"AuditOnly\" /></AppLockerPolicy>"));
    }

    // ------------------------------------------------------ assigned access

    private static string AssignedAccessConfig(string account = "Lucas", bool withApp = true) => $"""
        <AssignedAccessConfiguration xmlns="http://schemas.microsoft.com/AssignedAccess/2017/config">
          <Profiles>
            <Profile Id="9A2A5EFD-0000-0000-0000-000000000001">
              <AllAppsList><AllowedApps>
                {(withApp ? "<App AppUserModelId=\"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App\" />" : "")}
              </AllowedApps></AllAppsList>
            </Profile>
          </Profiles>
          <Configs>
            <Config><Account>{account}</Account>
              <DefaultProfile Id="9A2A5EFD-0000-0000-0000-000000000001"/>
            </Config>
          </Configs>
        </AssignedAccessConfiguration>
        """;

    [Fact]
    public void An_assigned_access_config_for_the_right_account_is_accepted() =>
        Assert.Null(AssignedAccessOperation.ValidateConfiguration(AssignedAccessConfig(), "Lucas"));

    [Fact]
    public void An_assigned_access_config_for_the_wrong_account_is_refused()
    {
        // Restricting the wrong person is the failure that matters here.
        var error = AssignedAccessOperation.ValidateConfiguration(AssignedAccessConfig("Jimmy"), "Lucas");

        Assert.NotNull(error);
        Assert.Contains("Lucas", error);
    }

    [Fact]
    public void An_assigned_access_config_with_no_apps_is_refused()
    {
        // A working login and nothing to do is not a child experience.
        var error = AssignedAccessOperation.ValidateConfiguration(AssignedAccessConfig(withApp: false), "Lucas");

        Assert.NotNull(error);
        Assert.Contains("inga program", error);
    }

    [Fact]
    public void A_machine_qualified_account_name_is_accepted() =>
        Assert.Null(AssignedAccessOperation.ValidateConfiguration(
            AssignedAccessConfig("TESTMACHINE\\Lucas"), "Lucas"));

    [Fact]
    public void Configurations_are_compared_structurally_not_as_text()
    {
        // The CSP normalises whitespace and attribute order, so a string
        // comparison would report a false failure on a correct configuration.
        var a = AssignedAccessConfig();
        var b = a.Replace("\n", " ").Replace("  ", " ");

        Assert.True(AssignedAccessOperation.SameConfiguration(a, b));
    }

    [Fact]
    public void A_different_app_list_is_not_the_same_configuration()
    {
        var a = AssignedAccessConfig();
        var b = a.Replace("WindowsCalculator", "Paint");

        Assert.False(AssignedAccessOperation.SameConfiguration(a, b));
    }
}

/// <summary>Assigned Access edition gating.</summary>
public class AssignedAccessGatingTests
{
    private static AssignedAccessOperation Build(WindowsSecurityCapabilities capabilities, bool childIsAdmin = false)
    {
        var child = FakeAccountService.Child(administrator: childIsAdmin);

        return new AssignedAccessOperation(
            new FakeToolRunner(),
            new FakeAccountService(FakeAccountService.Parent(), child),
            capabilities,
            child.Sid,
            "<AssignedAccessConfiguration><Configs><Config><Account>Lucas</Account></Config></Configs>" +
            "<Profiles><Profile><AllAppsList><AllowedApps><App AppUserModelId=\"a!b\"/>" +
            "</AllowedApps></AllAppsList></Profile></Profiles></AssignedAccessConfiguration>",
            new RecordingLogger());
    }

    private static WindowsSecurityCapabilities Capabilities(
        bool assignedAccess, bool? uac = true, string edition = "Windows 11 Pro") => new()
    {
        EditionDisplayName = edition,
        Edition = assignedAccess ? WindowsEdition.Pro : WindowsEdition.Home,
        Generation = WindowsGeneration.Windows11,
        AssignedAccess = assignedAccess ? CapabilityState.Available : CapabilityState.Unavailable,
        RecommendedSecurityMode = assignedAccess ? SecurityMode.Secure : SecurityMode.Standard,
        AppControl = new AppControlCapabilities
        {
            Enforcement = CapabilityState.Available,
            EnforcementService = CapabilityState.Available,
            PowerShellManagement = CapabilityState.Unavailable,
            LocalPolicyReadable = CapabilityState.Unavailable,
            LocalPolicyStore = CapabilityState.Unavailable,
            Csp = assignedAccess ? CapabilityState.Available : CapabilityState.Unavailable,
            ManagementUi = CapabilityState.Unavailable
        },
        IsUacEnabled = uac
    };

    [Fact]
    public async Task Home_is_told_plainly_that_it_cannot_do_this()
    {
        var outcome = await Build(Capabilities(assignedAccess: false, edition: "Windows 11 Home"))
            .PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("Windows 11 Home", outcome.Message);
        Assert.Contains("Pro", outcome.Message);
    }

    [Fact]
    public async Task Pro_is_allowed()
    {
        var outcome = await Build(Capabilities(assignedAccess: true)).PreflightAsync(ApplyContext.Create());

        Assert.True(outcome.Success);
    }

    [Fact]
    public async Task Assigned_access_is_refused_when_UAC_is_off()
    {
        // Microsoft requires UAC for a kiosk experience, and without it the
        // separate child account provides no real separation anyway.
        var outcome = await Build(Capabilities(assignedAccess: true, uac: false))
            .PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("UAC", outcome.Message);
    }

    [Fact]
    public async Task Assigned_access_is_refused_for_an_administrator_account()
    {
        var outcome = await Build(Capabilities(assignedAccess: true), childIsAdmin: true)
            .PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("administratör", outcome.Message);
    }
}

/// <summary>The watchdog service and the logout operation.</summary>
public class WatchdogAndLogoutTests
{
    private const string WatchdogPath = @"C:\Program Files\KidShell\KidShell.Watchdog.exe";

    [Fact]
    public async Task The_watchdog_refuses_a_path_that_is_not_absolute()
    {
        var operation = new WatchdogServiceOperation(
            new FakeServiceControl(), new FakeFileSystem(), @"watchdog.exe", new RecordingLogger());

        var outcome = await operation.PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("fullständig", outcome.Message);
    }

    [Fact]
    public async Task The_watchdog_refuses_a_path_that_does_not_exist()
    {
        var operation = new WatchdogServiceOperation(
            new FakeServiceControl(), new FakeFileSystem(), WatchdogPath, new RecordingLogger());

        var outcome = await operation.PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("hittades inte", outcome.Message);
    }

    [Fact]
    public async Task Installing_the_watchdog_registers_and_starts_it()
    {
        var services = new FakeServiceControl();
        var files = new FakeFileSystem();
        files.Seed(WatchdogPath, "binary");

        var operation = new WatchdogServiceOperation(services, files, WatchdogPath, new RecordingLogger());
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        var snapshot = await operation.CaptureStateAsync(context);
        Assert.False(snapshot.ExistedBefore);

        Assert.True((await operation.ApplyAsync(context)).Success);
        Assert.True((await operation.VerifyAsync(context)).Success);

        Assert.Equal(1, services.InstallCount);
    }

    [Fact]
    public async Task Rollback_removes_a_watchdog_that_KidShell_installed()
    {
        var services = new FakeServiceControl();
        var files = new FakeFileSystem();
        files.Seed(WatchdogPath, "binary");

        var operation = new WatchdogServiceOperation(services, files, WatchdogPath, new RecordingLogger());
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);

        Assert.True((await operation.RollbackAsync(snapshot, context)).Success);
        Assert.Equal(1, services.UninstallCount);
        Assert.False((await services.QueryAsync(WatchdogServiceOperation.ServiceName)).IsInstalled);
    }

    [Fact]
    public async Task Rollback_keeps_a_watchdog_that_was_already_installed()
    {
        var services = new FakeServiceControl();
        services.Seed(WatchdogServiceOperation.ServiceName, running: false, startType: "Manual");

        var files = new FakeFileSystem();
        files.Seed(WatchdogPath, "binary");

        var operation = new WatchdogServiceOperation(services, files, WatchdogPath, new RecordingLogger());
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        Assert.True(snapshot.ExistedBefore);

        await operation.ApplyAsync(context);
        Assert.True((await operation.RollbackAsync(snapshot, context)).Success);

        // Removing a service somebody else installed is not an undo.
        Assert.Equal(0, services.UninstallCount);
        Assert.Equal("Manual", (await services.QueryAsync(WatchdogServiceOperation.ServiceName)).StartType);
    }

    [Fact]
    public async Task A_watchdog_that_installs_but_does_not_run_fails_verification()
    {
        var services = new FakeServiceControl { StartSilentlyFails = true };
        var files = new FakeFileSystem();
        files.Seed(WatchdogPath, "binary");

        var operation = new WatchdogServiceOperation(services, files, WatchdogPath, new RecordingLogger());
        var context = ApplyContext.Create();

        await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);

        Assert.False((await operation.VerifyAsync(context)).Success);
    }

    [Fact]
    public void Signing_out_is_honestly_marked_as_irreversible()
    {
        var operation = new ChildSessionLogoutOperation(new FakeSessionControl(), new RecordingLogger());

        // The coordinator refuses any transaction containing an operation that
        // cannot roll back, and that refusal is the protection.
        Assert.False(operation.CanRollback);
    }

    [Fact]
    public async Task A_transaction_containing_the_logout_operation_is_refused()
    {
        var logger = new RecordingLogger();
        var store = new InMemoryManifestStore();

        var transaction = new SecurityTransaction(
            [new ChildSessionLogoutOperation(new FakeSessionControl(), logger)],
            store,
            new RecoveryMachineSummary
            {
                WindowsEdition = "Windows 11 Home", BuildNumber = 26200, MachineName = "TEST"
            },
            logger);

        var result = await transaction.ExecuteAsync(ApplyContext.Create());

        Assert.Equal(TransactionState.Refused, result.State);
        Assert.Contains("ångras", result.FailureMessage);
    }

    [Fact]
    public async Task Rolling_back_a_logout_is_refused_rather_than_pretended()
    {
        var operation = new ChildSessionLogoutOperation(new FakeSessionControl(), new RecordingLogger());
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);

        var rollback = await operation.RollbackAsync(snapshot, context);

        Assert.False(rollback.Success);
    }

    private sealed class InMemoryManifestStore : IRecoveryManifestStore
    {
        private readonly List<RecoveryManifest> _written = [];

        public Task<bool> WriteAsync(RecoveryManifest manifest, CancellationToken cancellationToken = default)
        {
            _written.Add(manifest);
            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(string transactionId, TransactionState finalState, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<RecoveryManifest>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecoveryManifest>>(_written);

        public Task<IReadOnlyList<RecoveryManifest>> ListOutstandingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecoveryManifest>>([]);
    }
}

/// <summary>Browser policy deployment.</summary>
public class BrowserPolicyOperationTests
{
    private static BrowserPolicy Policy(WebMode mode = WebMode.Allowlist) =>
        BrowserPolicyGenerator.Generate(
            new WebSettings { Mode = mode },
            [new AllowlistEntry { Host = "svt.se" }]);

    [Fact]
    public async Task Only_documented_Edge_policies_are_written()
    {
        var registry = new FakeRegistry();
        var operation = new BrowserPolicyOperation(registry, Policy(), new RecordingLogger());
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);

        // Every key written must be under the documented Edge policy path.
        Assert.All(registry.Values.Keys, key =>
            Assert.Contains(BrowserPolicyOperation.EdgePolicyKey, key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_policy_name_is_refused()
    {
        var policy = new BrowserPolicy
        {
            Mode = WebMode.Allowlist,
            Settings =
            [
                new BrowserPolicySetting { Name = "MakeEverythingFine", Value = "1", Reason = "invented" }
            ]
        };

        var operation = new BrowserPolicyOperation(new FakeRegistry(), policy, new RecordingLogger());
        var outcome = await operation.PreflightAsync(ApplyContext.Create());

        // The allowed set is a fixed list in code. "Whatever the generator
        // produced" is not a security boundary.
        Assert.False(outcome.Success);
        Assert.Contains("Okänd", outcome.Message);
    }

    [Fact]
    public async Task Rollback_removes_policies_that_were_not_there_before()
    {
        var registry = new FakeRegistry();
        var operation = new BrowserPolicyOperation(registry, Policy(), new RecordingLogger());
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        Assert.False(snapshot.ExistedBefore);

        await operation.ApplyAsync(context);
        Assert.True((await operation.RollbackAsync(snapshot, context)).Success);

        Assert.Empty(registry.Values);
    }

    [Fact]
    public async Task A_web_mode_with_no_policy_is_refused_rather_than_writing_nothing_silently()
    {
        var operation = new BrowserPolicyOperation(
            new FakeRegistry(), Policy(WebMode.NoBrowser), new RecordingLogger());

        var outcome = await operation.PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
    }
}
