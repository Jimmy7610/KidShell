using System.Reflection;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Operations;
using KidShell.WindowsIntegration.Platform;
using Xunit;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>
/// Builds an Apply-mode context by reflection, for tests only.
///
/// The product guarantee is that no public, internal or test-visible API
/// constructs an Apply context, and that remains exactly true: this reaches
/// past the language rather than calling something. It has to be deliberate and
/// ugly, so a future caller cannot do it by writing ordinary C#.
///
/// It also buys nothing real. An Apply context permits mutation; performing one
/// still needs a platform service, and every service in this test project is a
/// fake that writes to a dictionary.
/// </summary>
internal static class ApplyContext
{
    public static SecurityExecutionContext Create()
    {
        var constructor = typeof(SecurityExecutionContext).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: [typeof(SecurityExecutionMode)],
            modifiers: null);

        Assert.NotNull(constructor);

        var context = (SecurityExecutionContext)constructor!.Invoke([SecurityExecutionMode.Apply]);
        Assert.Equal(SecurityExecutionMode.Apply, context.Mode);
        return context;
    }
}

/// <summary>
/// The guarantee that applies to every operation, checked on every operation.
///
/// Written as a theory over the real types rather than one test each, so an
/// operation added later is covered the day it appears rather than the day
/// somebody remembers to write its test.
/// </summary>
public class EveryOperationTests
{
    public static TheoryData<ISecurityOperation> AllOperations() => [.. AllOperationList()];

    internal static IReadOnlyList<ISecurityOperation> AllOperationList()
    {
        var logger = new RecordingLogger();
        var accounts = new FakeAccountService(FakeAccountService.Parent(), FakeAccountService.Child());
        var registry = new FakeRegistry();
        var tools = new FakeToolRunner();
        var services = new FakeServiceControl();
        var files = new FakeFileSystem();

        return
        [
            new CreateChildAccountOperation(accounts, "Nils", "Nils", null, logger),
            new DemoteChildAccountOperation(accounts, FakeAccountService.Child().Sid, logger),
            new ChildAutostartOperation(registry, FakeAccountService.Child().Sid, "Pkg_abc!App", logger),
            new AppLockerDeploymentOperation(tools, files, "<AppLockerPolicy/>", @"C:\temp", logger),
            new ApplicationIdentityServiceOperation(services, tools, files, @"C:\temp", logger),
            new WatchdogServiceOperation(services, files, @"C:\KidShell\watchdog.exe", logger),
            new ChildSessionLogoutOperation(new FakeSessionControl(), logger)
        ];
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    public async Task No_operation_applies_without_an_apply_context(ISecurityOperation operation)
    {
        // The guarantee that makes this whole assembly safe to ship in a build
        // that must not change Windows. Every operation refuses AuditOnly, and
        // AuditOnly is the only context KidShell can construct.
        var outcome = await operation.ApplyAsync(SecurityExecutionContext.AuditOnly());

        Assert.False(outcome.Success);
        Assert.Contains("granskningsläge", outcome.Message);
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    public void Every_operation_has_parent_facing_wording(ISecurityOperation operation)
    {
        Assert.False(string.IsNullOrWhiteSpace(operation.Description));
        Assert.DoesNotContain("0x", operation.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", operation.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HKEY", operation.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    public void Every_operation_has_a_stable_id(ISecurityOperation operation)
    {
        Assert.False(string.IsNullOrWhiteSpace(operation.Id));
        Assert.DoesNotContain(' ', operation.Id);
    }

    [Fact]
    public void Only_the_logout_operation_refuses_to_roll_back()
    {
        // Every reversible operation may join a transaction. The one that
        // cannot - signing a session out - must say so, because the coordinator
        // refuses any transaction containing it and that refusal is the
        // protection.
        foreach (var operation in AllOperationList())
        {
            var expected = operation.Id != "child-session-logout";
            Assert.Equal(expected, operation.CanRollback);
        }
    }
}

/// <summary>Creating and rolling back the child's Windows account.</summary>
public class ChildAccountOperationTests
{
    private static CreateChildAccountOperation Create(FakeAccountService accounts, string name = "Nils") =>
        new(accounts, name, name, null, new RecordingLogger());

    [Fact]
    public async Task A_child_account_is_created_as_a_standard_user()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent());
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        var snapshot = await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);
        Assert.True((await operation.VerifyAsync(context)).Success);

        var created = await accounts.FindByNameAsync("Nils");

        Assert.NotNull(created);
        Assert.False(created!.IsAdministrator);
        Assert.True(created.IsEnabled);
        Assert.NotEmpty(created.Sid);

        // Created standard, never created as an administrator and demoted -
        // that would leave a window with rights nobody intended.
        Assert.Equal(0, accounts.SetAdministratorCount);
        Assert.False(snapshot.ExistedBefore);
    }

    [Fact]
    public async Task Creation_is_refused_when_no_recovery_administrator_would_remain()
    {
        // The invariant that outranks everything else in this codebase.
        var accounts = new FakeAccountService(FakeAccountService.Child());

        var outcome = await Create(accounts).PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("administratörskonto", outcome.Message);
    }

    [Fact]
    public async Task A_disabled_administrator_does_not_count_as_a_way_back_in()
    {
        // The built-in Administrator is disabled by default on a consumer
        // machine. Treating it as recovery would be counting on an account
        // nobody can sign in to.
        var accounts = new FakeAccountService(
            FakeAccountService.BuiltInAdministrator(),
            FakeAccountService.Child());

        var outcome = await Create(accounts).PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task Creation_is_refused_when_the_name_is_taken()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent(), FakeAccountService.Child());

        var outcome = await Create(accounts, "Lucas").PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("redan", outcome.Message);
    }

    [Fact]
    public async Task Rollback_deletes_the_account_this_operation_created()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent());
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);
        await operation.VerifyAsync(context);

        var rollback = await operation.RollbackAsync(snapshot, context);

        Assert.True(rollback.Success);
        Assert.Null(await accounts.FindByNameAsync("Nils"));
    }

    [Fact]
    public async Task Rollback_refuses_to_delete_an_account_that_predates_KidShell()
    {
        // Deleting an account a family has had for years, in the name of
        // "undo", would destroy a profile and everything in it.
        var accounts = new FakeAccountService(FakeAccountService.Parent());
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        await operation.ApplyAsync(context);

        var pretendItExisted = new OperationSnapshot
        {
            OperationId = operation.Id,
            Description = "existed",
            ExistedBefore = true
        };

        var rollback = await operation.RollbackAsync(pretendItExisted, context);

        Assert.False(rollback.Success);
        Assert.Equal(0, accounts.DeleteCount);
    }

    [Fact]
    public async Task Rollback_reports_failure_when_the_account_survives()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent()) { DeleteSilentlyFails = true };
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);

        var rollback = await operation.RollbackAsync(snapshot, context);

        // Read back, not assumed. A rollback that says it worked when it did
        // not is worse than one that admits failure.
        Assert.False(rollback.Success);
    }

    [Fact]
    public async Task Nothing_is_rolled_back_when_nothing_was_applied()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent());
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        var rollback = await operation.RollbackAsync(snapshot, context);

        Assert.True(rollback.Success);
        Assert.Equal(0, accounts.DeleteCount);
    }

    [Fact]
    public async Task A_creation_that_throws_is_a_failed_outcome_not_an_escape()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent())
        {
            ThrowOnCreate = new InvalidOperationException("netapi32 said no")
        };

        var outcome = await Create(accounts).ApplyAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.DoesNotContain("netapi32", outcome.Message);
        Assert.Contains("netapi32", outcome.Detail ?? string.Empty);
    }

    [Fact]
    public async Task Demotion_is_refused_for_the_only_administrator()
    {
        var parent = FakeAccountService.Parent();
        var accounts = new FakeAccountService(parent);

        var operation = new DemoteChildAccountOperation(accounts, parent.Sid, new RecordingLogger());
        var outcome = await operation.PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("enda administratörskontot", outcome.Message);
    }

    [Fact]
    public async Task Demotion_removes_the_role_and_restores_it_on_rollback()
    {
        var child = FakeAccountService.Child(administrator: true);
        var accounts = new FakeAccountService(FakeAccountService.Parent(), child);
        var operation = new DemoteChildAccountOperation(accounts, child.Sid, new RecordingLogger());
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        var snapshot = await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);
        Assert.True((await operation.VerifyAsync(context)).Success);

        Assert.False((await accounts.FindBySidAsync(child.Sid))!.IsAdministrator);

        Assert.True((await operation.RollbackAsync(snapshot, context)).Success);
        Assert.True((await accounts.FindBySidAsync(child.Sid))!.IsAdministrator);
    }

    [Fact]
    public async Task Verification_fails_if_demotion_would_leave_nobody_able_to_sign_in()
    {
        // Contrived, and checked anyway: the state after the change is what
        // matters, not the state preflight predicted.
        var child = FakeAccountService.Child(administrator: true);
        var accounts = new FakeAccountService(FakeAccountService.Parent(enabled: false), child);
        var operation = new DemoteChildAccountOperation(accounts, child.Sid, new RecordingLogger());
        var context = ApplyContext.Create();

        await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);

        var verify = await operation.VerifyAsync(context);

        Assert.False(verify.Success);
        Assert.Contains("administratörskonto", verify.Message);
    }
}

/// <summary>Per-child autostart.</summary>
public class AutostartOperationTests
{
    private const string ChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Aumid = "KidShell.Barnlage.Dev_8wekyb3d8bbwe!App";

    private static ChildAutostartOperation Create(FakeRegistry registry) =>
        new(registry, ChildSid, Aumid, new RecordingLogger());

    [Fact]
    public async Task Autostart_is_written_to_the_childs_hive_and_never_machine_wide()
    {
        var registry = new FakeRegistry();
        registry.SeedKey(RegistryScope.NamedUser, ChildSid, ChildAutostartOperation.RunKey);

        var operation = Create(registry);
        var context = ApplyContext.Create();

        await operation.PreflightAsync(context);
        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);
        Assert.True((await operation.VerifyAsync(context)).Success);

        // Every written key must be the child's. A machine-wide Run value would
        // launch KidShell for the parent too.
        Assert.All(registry.Values.Keys, key =>
        {
            Assert.StartsWith($"{RegistryScope.NamedUser}|{ChildSid}|", key, StringComparison.Ordinal);
            Assert.DoesNotContain(RegistryScope.LocalMachine.ToString(), key, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_launch_command_uses_the_documented_AppsFolder_route()
    {
        var registry = new FakeRegistry();
        var operation = Create(registry);

        // A packaged app has no .exe to point at; this is how the Start menu
        // launches one.
        Assert.Equal($"explorer.exe shell:AppsFolder\\{Aumid}", operation.LaunchCommand);

        await operation.CaptureStateAsync(ApplyContext.Create());
        await operation.ApplyAsync(ApplyContext.Create());

        var written = await registry.ReadStringAsync(
            RegistryScope.NamedUser, ChildSid, ChildAutostartOperation.RunKey, ChildAutostartOperation.ValueName);

        Assert.Equal(operation.LaunchCommand, written);
    }

    [Fact]
    public async Task Rollback_restores_a_value_that_was_there_before()
    {
        var registry = new FakeRegistry();
        registry.Seed(RegistryScope.NamedUser, ChildSid, ChildAutostartOperation.RunKey,
            ChildAutostartOperation.ValueName, @"C:\Something\Else.exe");

        var operation = Create(registry);
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        Assert.True(snapshot.ExistedBefore);

        await operation.ApplyAsync(context);
        Assert.True((await operation.RollbackAsync(snapshot, context)).Success);

        var restored = await registry.ReadStringAsync(
            RegistryScope.NamedUser, ChildSid, ChildAutostartOperation.RunKey, ChildAutostartOperation.ValueName);

        // Exactly what the family had, not "deleted because KidShell wrote it".
        Assert.Equal(@"C:\Something\Else.exe", restored);
    }

    [Fact]
    public async Task Rollback_removes_the_value_when_there_was_none_before()
    {
        var registry = new FakeRegistry();
        var operation = Create(registry);
        var context = ApplyContext.Create();

        var snapshot = await operation.CaptureStateAsync(context);
        await operation.ApplyAsync(context);

        Assert.True((await operation.RollbackAsync(snapshot, context)).Success);

        Assert.Null(await registry.ReadStringAsync(
            RegistryScope.NamedUser, ChildSid, ChildAutostartOperation.RunKey, ChildAutostartOperation.ValueName));
    }

    [Fact]
    public async Task Verification_fails_when_the_write_did_not_stick()
    {
        // Windows can accept a write and not honour it, so Apply returning
        // success is a claim and Verify is the evidence.
        var registry = new FakeRegistry { WritesAreLost = true };
        var operation = Create(registry);
        var context = ApplyContext.Create();

        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);

        Assert.False((await operation.VerifyAsync(context)).Success);
    }

    [Fact]
    public async Task A_missing_child_hive_is_explained_rather_than_failing_at_apply()
    {
        var operation = new ChildAutostartOperation(
            new ThrowingRegistry(), ChildSid, Aumid, new RecordingLogger());

        var outcome = await operation.PreflightAsync(ApplyContext.Create());

        Assert.False(outcome.Success);
        Assert.Contains("Logga in på barnkontot", outcome.Message);
    }

    private sealed class ThrowingRegistry : IRegistryStore
    {
        public Task<bool> KeyExistsAsync(RegistryScope scope, string? userSid, string subKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("hive not loaded");

        public Task<string?> ReadStringAsync(RegistryScope scope, string? userSid, string subKey, string valueName, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<int?> ReadDWordAsync(RegistryScope scope, string? userSid, string subKey, string valueName, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task WriteStringAsync(RegistryScope scope, string? userSid, string subKey, string valueName, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteDWordAsync(RegistryScope scope, string? userSid, string subKey, string valueName, int value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteValueAsync(RegistryScope scope, string? userSid, string subKey, string valueName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteKeyAsync(RegistryScope scope, string? userSid, string subKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
