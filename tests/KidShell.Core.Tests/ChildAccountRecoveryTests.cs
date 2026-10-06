using KidShell.Core.Security.Validation;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// THE PARTIAL ACCOUNT ON WILMA.
///
/// `apply\01-create-child-account.ps1 -Apply` created KidShellChild (SID
/// ...-1003, enabled) and then failed to add it to the built-in Users group:
/// `Add-LocalGroupMember -Member` is typed LocalPrincipal[], LocalPrincipal only
/// constructs from a string, and the script passed a SecurityIdentifier object -
/// so parameter binding failed before Windows was ever asked to do anything.
///
/// The script printed that as a yellow note, then printed "Created
/// 'KidShellChild'." in green and exited 0. An operator reading the last line
/// would have moved on to Part 4 with a half-configured machine.
///
/// And the script's first statement was `if ($existing) { ...; return }`, so
/// re-running it after a fix would have reported the broken account as fine and
/// repaired nothing. The only exits were editing Windows by hand or deleting the
/// child account, which on a real device destroys a profile.
///
/// Every state below is one the script can actually be run in. The ones that
/// refuse are as important as the ones that repair: this script owns the standard
/// Users membership and nothing else, and a repair that reached further would be
/// a script quietly making security decisions about a machine whose history it
/// does not know.
/// </summary>
public class ChildAccountRecoveryTests
{
    private const string ChildSid = "S-1-5-21-3382208030-1057815629-3599114088-1003";

    /// <summary>The state WILMA is in right now: exists, enabled, correct SID, not in Users.</summary>
    private static ChildAccountFacts Wilma() => new()
    {
        ConfiguredName = "KidShellChild",
        Exists = true,
        Enabled = true,
        Sid = ChildSid,
        ExpectedSid = ChildSid,
        InStandardUsersGroup = false,
        EnabledRecoveryAdministratorExists = true
    };

    // ------------------------------------------------------- WILMA's state

    [Fact]
    public void Wilmas_exact_state_repairs_only_the_users_membership()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma());

        Assert.Equal(ChildAccountAction.Repair, plan.Action);
        Assert.True(plan.AddStandardUsersMembership);

        // The account exists. Asking for a password would imply recreation, and
        // the SID must not change - every ACL on the device is scoped to it.
        Assert.False(plan.NeedsPassword);
        Assert.False(plan.Refused);
    }

    [Fact]
    public void Wilmas_state_is_not_reported_as_already_finished()
    {
        // The defect: the old script's first statement was "it exists, so
        // return", which would have called this state done.
        var plan = ChildAccountSetupPlan.Decide(Wilma());

        Assert.NotEqual(ChildAccountAction.None, plan.Action);
    }

    // --------------------------------------------------------------- case A

    [Fact]
    public void A_absent_account_is_created_and_added_to_users()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            Exists = false, Enabled = false, Sid = "", ExpectedSid = "", InStandardUsersGroup = null
        });

        Assert.Equal(ChildAccountAction.Create, plan.Action);
        Assert.True(plan.AddStandardUsersMembership);
        Assert.True(plan.NeedsPassword);
    }

    // --------------------------------------------------------------- case B

    [Fact]
    public void B_account_that_is_already_correct_is_a_no_op_success()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with { InStandardUsersGroup = true });

        Assert.Equal(ChildAccountAction.None, plan.Action);
        Assert.Equal(ChildAccountResult.Success, plan.ResultIfActionSucceeds);

        // No password prompt on a no-op: being asked for one would suggest the
        // account is about to be recreated.
        Assert.False(plan.NeedsPassword);
        Assert.False(plan.AddStandardUsersMembership);
    }

    // --------------------------------------------------------------- case C

    [Fact]
    public void C_missing_users_membership_is_repaired_and_nothing_else_is()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma());

        Assert.Equal(ChildAccountAction.Repair, plan.Action);
        Assert.True(plan.AddStandardUsersMembership);
        Assert.False(plan.NeedsPassword);
    }

    // --------------------------------------------------------------- case D

    [Theory]
    [InlineData("S-1-5-32-544", "Administrators")]
    [InlineData("S-1-5-32-547", "Power Users")]
    [InlineData("S-1-5-32-551", "Backup Operators")]
    [InlineData("S-1-5-32-555", "Remote Desktop Users")]
    [InlineData("S-1-5-32-578", "Hyper-V Administrators")]
    public void D_a_privileged_child_account_is_refused_and_the_group_is_named(
        string privilegedSid, string label)
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            InStandardUsersGroup = true,
            PrivilegedGroupSids = [privilegedSid]
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.Equal(ChildAccountResult.Refused, plan.ResultIfActionSucceeds);

        // The operator has to be told exactly what was found, by SID and by
        // canonical label - the machine's own name for it is localized.
        Assert.Contains(plan.Reasons, r => r.Contains(privilegedSid, StringComparison.Ordinal));
        Assert.Contains(plan.Reasons, r => r.Contains(label, StringComparison.Ordinal));
    }

    [Fact]
    public void D_privilege_is_never_removed_automatically()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            InStandardUsersGroup = false,
            PrivilegedGroupSids = ["S-1-5-32-544"]
        });

        // Privilege outranks the repair: it does not quietly add Users
        // membership to an account that is also an administrator.
        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.False(plan.AddStandardUsersMembership);
    }

    // --------------------------------------------------------------- case E

    [Fact]
    public void E_a_disabled_account_is_refused_rather_than_enabled()
    {
        // Defined behaviour, not an accident: enabling an account grants a
        // sign-in, and it may have been disabled deliberately.
        var plan = ChildAccountSetupPlan.Decide(Wilma() with { Enabled = false });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.Contains(plan.Reasons, r => r.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void E_a_disabled_account_is_refused_even_when_everything_else_is_right()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            Enabled = false, InStandardUsersGroup = true
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
    }

    // --------------------------------------------------------------- case F

    [Fact]
    public void F_a_failed_users_add_after_creating_is_PARTIAL_not_success()
    {
        // The exact WILMA outcome. It must never be Success.
        var result = ChildAccountSetupPlan.ResultOfFailedMembership(accountWasJustCreated: true);

        Assert.Equal(ChildAccountResult.Partial, result);
        Assert.NotEqual(0, ChildAccountSetupPlan.ExitCodeOf(result));
    }

    [Fact]
    public void F_a_failed_users_add_during_a_repair_is_a_failure()
    {
        var result = ChildAccountSetupPlan.ResultOfFailedMembership(accountWasJustCreated: false);

        Assert.Equal(ChildAccountResult.Failed, result);
        Assert.NotEqual(0, ChildAccountSetupPlan.ExitCodeOf(result));
    }

    [Fact]
    public void Every_non_success_result_has_a_distinct_non_zero_exit_code()
    {
        var codes = new[]
        {
            ChildAccountResult.Success, ChildAccountResult.Partial,
            ChildAccountResult.Refused, ChildAccountResult.Failed
        }.Select(ChildAccountSetupPlan.ExitCodeOf).ToList();

        Assert.Equal(0, ChildAccountSetupPlan.ExitCodeOf(ChildAccountResult.Success));

        // Distinct, because "refused on purpose" and "left the machine half
        // configured" need different responses and a bare 1 cannot say which.
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.DoesNotContain(0, codes.Skip(1));
    }

    // --------------------------------------------------------------- case G

    [Fact]
    public void G_rerunning_after_a_partial_failure_repairs_rather_than_reporting_done()
    {
        // The second run sees exactly what WILMA has now.
        var secondRun = ChildAccountSetupPlan.Decide(Wilma());

        Assert.Equal(ChildAccountAction.Repair, secondRun.Action);

        // And the run after a successful repair is a no-op, so a third run is
        // harmless too.
        var thirdRun = ChildAccountSetupPlan.Decide(Wilma() with { InStandardUsersGroup = true });

        Assert.Equal(ChildAccountAction.None, thirdRun.Action);
        Assert.Equal(ChildAccountResult.Success, thirdRun.ResultIfActionSucceeds);
    }

    [Fact]
    public void G_the_repair_never_asks_to_recreate_the_account()
    {
        // The SID must survive: every ACL, every registry scope and the config's
        // ExpectedChildSid are scoped to it.
        var plan = ChildAccountSetupPlan.Decide(Wilma());

        Assert.NotEqual(ChildAccountAction.Create, plan.Action);
        Assert.False(plan.NeedsPassword);
    }

    // --------------------------------------------------------------- case H

    [Fact]
    public void H_the_decision_never_depends_on_a_localized_group_name()
    {
        // Groups arrive as SIDs and are reported with canonical English labels.
        // Nothing in the decision reads a display name, so a Swedish machine and
        // an English one decide identically.
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            PrivilegedGroupSids = ["S-1-5-32-544"]
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.Contains(plan.Reasons, r => r.Contains("S-1-5-32-544", StringComparison.Ordinal));

        // "Administratörer" would be the machine's own word for it, and it must
        // appear nowhere in a decision.
        Assert.DoesNotContain(plan.Reasons, r => r.Contains("Administratörer", StringComparison.Ordinal));
    }

    [Fact]
    public void H_the_users_group_is_always_named_by_sid_in_the_reasons()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma());

        Assert.Contains(plan.Reasons, r => r.Contains(WellKnownSecurityGroups.UsersSid, StringComparison.Ordinal));
    }

    // -------------------------------------------- the invariant above all

    [Fact]
    public void No_recovery_administrator_refuses_before_anything_else_is_considered()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            EnabledRecoveryAdministratorExists = false
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);

        // Checked first, so this is the only reason given - the operator is not
        // left reading about group membership when the machine is one step from
        // having nobody who can sign in.
        Assert.Single(plan.Reasons);
    }

    [Fact]
    public void No_recovery_administrator_refuses_even_when_there_is_nothing_to_do()
    {
        // A run that reported "all good" here would tell the operator the
        // opposite of what they need to know.
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            InStandardUsersGroup = true,
            EnabledRecoveryAdministratorExists = false
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
    }

    // --------------------------------------------------- unanswered questions

    [Fact]
    public void Unknown_users_membership_is_not_treated_as_missing()
    {
        // Adding on a guess would be a write based on a question that was never
        // answered.
        var plan = ChildAccountSetupPlan.Decide(Wilma() with { InStandardUsersGroup = null });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.False(plan.AddStandardUsersMembership);
    }

    [Fact]
    public void An_unreadable_privileged_group_is_not_treated_as_clean()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            UnreadablePrivilegedGroupSids = ["S-1-5-32-544"]
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.Contains(plan.Reasons, r => r.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_absent_privileged_group_is_not_an_obstacle()
    {
        // Windows Home has no Power Users, Backup Operators or Remote Desktop
        // Users. A script that treated their absence as a problem would fail on
        // the edition most families run - so absence reaches the plan as neither
        // a membership nor an unreadable group.
        var plan = ChildAccountSetupPlan.Decide(Wilma());

        Assert.Equal(ChildAccountAction.Repair, plan.Action);
    }

    // ----------------------------------------------------- identity mismatch

    [Fact]
    public void A_sid_that_does_not_match_the_config_is_refused()
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with
        {
            ExpectedSid = "S-1-5-21-9999999999-8888888888-7777777777-1001"
        });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.Contains(plan.Reasons, r => r.Contains("different account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_empty_expected_sid_is_ordinary_and_does_not_refuse()
    {
        // On a first run the SID is not known until the account exists, so an
        // empty ExpectedChildSid must not be mistaken for a mismatch.
        var plan = ChildAccountSetupPlan.Decide(Wilma() with { ExpectedSid = "" });

        Assert.Equal(ChildAccountAction.Repair, plan.Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    [InlineData("KidShellChild")]
    public void An_account_whose_sid_cannot_be_read_is_not_touched(string sid)
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with { Sid = sid, ExpectedSid = "" });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
        Assert.False(plan.AddStandardUsersMembership);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_config_with_no_child_name_refuses(string name)
    {
        var plan = ChildAccountSetupPlan.Decide(Wilma() with { ConfiguredName = name });

        Assert.Equal(ChildAccountAction.Refuse, plan.Action);
    }

    [Fact]
    public void Every_plan_explains_itself()
    {
        // A refusal nobody can act on is a dead end, and a repair nobody can
        // audit is worse.
        ChildAccountFacts[] states =
        [
            Wilma(),
            Wilma() with { Exists = false },
            Wilma() with { InStandardUsersGroup = true },
            Wilma() with { Enabled = false },
            Wilma() with { PrivilegedGroupSids = ["S-1-5-32-544"] },
            Wilma() with { InStandardUsersGroup = null },
            Wilma() with { EnabledRecoveryAdministratorExists = false }
        ];

        foreach (var state in states)
        {
            Assert.NotEmpty(ChildAccountSetupPlan.Decide(state).Reasons);
        }
    }
}
