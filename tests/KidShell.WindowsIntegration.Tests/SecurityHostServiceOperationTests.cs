using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Operations;
using Xunit;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>
/// Installing the security service, against a fake service control manager.
///
/// NOTHING HERE INSTALLS A SERVICE. Every call goes to an in-memory
/// dictionary, and the operation refuses to apply at all without an
/// Apply-mode context that no KidShell build can construct. The preflight
/// and the rollback are what these tests exercise, because those are the
/// parts that have to be right before anyone runs this on a real device.
/// </summary>
public class SecurityHostServiceOperationTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string Image = @"C:\Program Files\KidShell\KidShell.SecurityHost.exe";

    private static (SecurityHostServiceOperation Operation, FakeServiceControl Services, FakeFileSystem Files)
        Build(string image = Image, Action<FakeServiceControl>? seed = null)
    {
        var services = new FakeServiceControl();
        seed?.Invoke(services);

        var files = new FakeFileSystem();
        files.Seed(Image, "binary");

        return (
            new SecurityHostServiceOperation(services, files, image, ProgramFiles, new RecordingLogger()),
            services, files);
    }

    private static SecurityExecutionContext Audit() => SecurityExecutionContext.AuditOnly();

    // ---------------------------------------------------------- identity

    [Fact]
    public void The_service_identity_is_the_one_the_documentation_states()
    {
        Assert.Equal("KidShellSecurityHost", SecurityHostService.Name);
        Assert.Equal("KidShell Security Host", SecurityHostService.DisplayName);
        Assert.Equal("LocalSystem", SecurityHostService.Account);
        Assert.Equal("auto", SecurityHostService.StartType);
    }

    [Fact]
    public void It_does_not_collide_with_the_watchdog()
    {
        // Two LocalSystem services with different jobs. Giving the watchdog
        // an endpoint would turn "restart the shell" into "do what this
        // message says, as SYSTEM"; giving the broker a restart loop would
        // put a privileged request handler in the one component that must
        // never be interesting to talk to.
        Assert.NotEqual(WatchdogServiceOperation.ServiceName, SecurityHostService.Name);
    }

    // --------------------------------------------------------- preflight

    [Fact]
    public async Task A_clean_machine_passes_preflight()
    {
        var (operation, _, _) = Build();

        var outcome = await operation.PreflightAsync(Audit());

        Assert.True(outcome.Success, outcome.Message);
    }

    [Theory]
    [InlineData(@"C:\Users\barn\AppData\Local\KidShell.SecurityHost.exe")]
    [InlineData(@"C:\Program Files\KidShell\..\..\Users\barn\KidShell.SecurityHost.exe")]
    [InlineData(@"KidShell.SecurityHost.exe")]
    [InlineData(@"C:\Program Files\KidShell\something-else.exe")]
    public async Task An_image_the_child_could_reach_is_refused(string image)
    {
        // A LocalSystem service whose binary the child can replace is a
        // privilege escalation with a service name, and no access list on
        // the pipe would matter.
        var (operation, _, _) = Build(image);

        var outcome = await operation.PreflightAsync(Audit());

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task A_missing_binary_is_refused()
    {
        var services = new FakeServiceControl();
        var operation = new SecurityHostServiceOperation(
            services, new FakeFileSystem(), Image, ProgramFiles, new RecordingLogger());

        var outcome = await operation.PreflightAsync(Audit());

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task An_existing_service_is_reconfigured_rather_than_refused()
    {
        var (operation, _, _) = Build(seed: s => s.Seed(SecurityHostService.Name, true, "Manual"));

        var outcome = await operation.PreflightAsync(Audit());

        Assert.True(outcome.Success, outcome.Message);
    }

    // -------------------------------------------------------- the gate

    [Fact]
    public async Task It_refuses_to_apply_in_the_only_context_that_exists()
    {
        var (operation, services, _) = Build();

        var outcome = await operation.ApplyAsync(Audit());

        Assert.False(outcome.Success);
        Assert.Equal(0, services.InstallCount);
    }

    [Fact]
    public void It_is_recorded_as_higher_risk_than_the_watchdog()
    {
        var (operation, _, _) = Build();

        var watchdog = new WatchdogServiceOperation(
            new FakeServiceControl(), new FakeFileSystem(), @"C:\KidShell\watchdog.exe",
            new RecordingLogger());

        // This one installs a LocalSystem service that accepts requests.
        // That one installs a LocalSystem service that accepts nothing.
        Assert.True(operation.RiskLevel > watchdog.RiskLevel);
    }

    // ----------------------------------------------------------- verify

    [Fact]
    public async Task An_installed_service_that_is_not_running_fails_verification()
    {
        // Worse than a missing one: the product would look configured and
        // every protected write would fail.
        var (operation, _, _) = Build(seed: s => s.Seed(SecurityHostService.Name, false, "Automatic"));

        var outcome = await operation.VerifyAsync(Audit());

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task A_running_service_that_does_not_start_automatically_fails_verification()
    {
        // A named pipe belongs to whoever creates it first, so a service
        // that starts after an interactive logon leaves a window in which
        // something running as the child can stand up the endpoint itself.
        var (operation, _, _) = Build(seed: s => s.Seed(SecurityHostService.Name, true, "Manual"));

        var outcome = await operation.VerifyAsync(Audit());

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task A_running_automatic_service_verifies()
    {
        var (operation, _, _) = Build(seed: s => s.Seed(SecurityHostService.Name, true, "Automatic"));

        var outcome = await operation.VerifyAsync(Audit());

        Assert.True(outcome.Success, outcome.Message);
    }

    // --------------------------------------------------------- rollback

    [Fact]
    public async Task Rollback_without_an_apply_touches_nothing()
    {
        // WHAT CANNOT BE TESTED HERE, AND WHY IT IS NOT HIDDEN.
        //
        // The rollback body - stop, uninstall, or restore the previous start
        // type - cannot be driven from a test. The base class refuses to roll
        // back an operation it did not apply, and applying needs an
        // Apply-mode context that this solution has no public, internal or
        // test-visible way to construct. That property is deliberate and
        // worth more than the coverage: weakening it to reach this code would
        // mean a mutating call could appear by accident.
        //
        // So the rollback paths are written, reviewed and unexercised, and
        // the report says so under remaining blockers. What IS provable is
        // the part that keeps a test run from removing a real service: a
        // rollback with nothing applied does nothing at all.
        var (operation, services, _) = Build(seed: s => s.Seed(SecurityHostService.Name, true, "Automatic"));

        var outcome = await operation.RollbackAsync(
            new OperationSnapshot
            {
                OperationId = operation.Id,
                Description = "nothing was installed",
                ExistedBefore = false
            },
            Audit());

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(0, services.UninstallCount);
        Assert.True((await services.QueryAsync(SecurityHostService.Name)).IsInstalled);
        Assert.True((await services.QueryAsync(SecurityHostService.Name)).IsRunning);
    }

    [Fact]
    public async Task A_missing_service_is_reported_rather_than_assumed_present()
    {
        // The recovery case a parent would actually meet: the service was
        // removed, or never installed, and the product has to notice.
        var (operation, _, _) = Build();

        var outcome = await operation.VerifyAsync(Audit());

        Assert.False(outcome.Success);
        Assert.Contains("kunde inte hittas", outcome.Message, StringComparison.Ordinal);
    }
}
