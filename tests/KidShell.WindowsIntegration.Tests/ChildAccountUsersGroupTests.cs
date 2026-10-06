using KidShell.WindowsIntegration.Operations;
using Xunit;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>
/// The production equivalent of the WILMA defect.
///
/// The validation script created the child account and then failed to put it in
/// the built-in Users group, and reported the stage as finished anyway. This
/// operation could not fail the same way for the same reason - it uses NetUserAdd
/// rather than Add-LocalGroupMember - but it could reach the same END STATE and
/// report it the same way: it relied on NetUserAdd's documented behaviour of
/// placing a USER_PRIV_USER account in the local Users group, and never looked.
///
/// An account that is not in Users cannot run anything a standard user runs, so
/// "created" without it is not a finished operation. These tests hold the check
/// that was missing.
/// </summary>
public class ChildAccountUsersGroupTests
{
    private static CreateChildAccountOperation Create(FakeAccountService accounts, string name = "Nils") =>
        new(accounts, name, name, null, new RecordingLogger());

    [Fact]
    public async Task Verification_asks_whether_the_account_is_in_the_standard_users_group()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent());
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);
        Assert.True((await operation.VerifyAsync(context)).Success);

        // Asked, not assumed. Before this, the membership the whole account
        // depends on was never read back.
        Assert.True(accounts.UsersGroupQueryCount > 0);
    }

    [Fact]
    public async Task An_account_created_outside_the_users_group_fails_verification()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent())
        {
            InStandardUsersGroup = false
        };

        var operation = Create(accounts);
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);

        // Apply succeeded - the account really was created. Verification is
        // where the half-finished state has to be caught, and it must not be
        // reported as a completed operation.
        var verify = await operation.VerifyAsync(context);

        Assert.False(verify.Success);
        Assert.Contains("Users", verify.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unreadable_membership_fails_verification_rather_than_passing_it()
    {
        var accounts = new FakeAccountService(FakeAccountService.Parent())
        {
            // "Could not read", which is not "not a member" and is certainly not
            // "a member".
            InStandardUsersGroup = null
        };

        var operation = Create(accounts);
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);

        var verify = await operation.VerifyAsync(context);

        Assert.False(verify.Success);
    }

    [Fact]
    public async Task A_normally_created_account_still_verifies()
    {
        // The guard must not make the ordinary case fail: NetUserAdd does place
        // a standard account in Users, and that is what the fake reports by
        // default.
        var accounts = new FakeAccountService(FakeAccountService.Parent());
        var operation = Create(accounts);
        var context = ApplyContext.Create();

        Assert.True((await operation.PreflightAsync(context)).Success);
        await operation.CaptureStateAsync(context);
        Assert.True((await operation.ApplyAsync(context)).Success);
        Assert.True((await operation.VerifyAsync(context)).Success);
    }
}
