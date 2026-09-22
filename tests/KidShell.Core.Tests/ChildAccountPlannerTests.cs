using KidShell.Core.Security.Accounts;
using KidShell.Core.Security.Readiness;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Planning the child's Windows account.
///
/// The invariant every test here circles: an enabled administrator that is not
/// the child's account must survive. Everything else is a preference; that one
/// is the difference between a locked-down machine and a brick.
///
/// Nothing in these tests creates, modifies or deletes a Windows account, and
/// nothing in KidShell can.
/// </summary>
public class ChildAccountPlannerTests
{
    private const string ParentSid = "S-1-5-21-1-2-3-1001";
    private const string ChildSid = "S-1-5-21-1-2-3-1002";

    private static WindowsAccount Admin(string name = "Jimmy", string sid = ParentSid, bool enabled = true) => new()
    {
        Username = name,
        Sid = sid,
        IsAdministrator = true,
        IsEnabled = enabled,
        IsBuiltIn = false
    };

    private static WindowsAccount Standard(string name = "Barn", string sid = ChildSid, bool enabled = true) => new()
    {
        Username = name,
        Sid = sid,
        IsAdministrator = false,
        IsEnabled = enabled,
        IsBuiltIn = false
    };

    private static WindowsAccount BuiltIn(string name = "DefaultAccount") => new()
    {
        Username = name,
        Sid = "S-1-5-21-1-2-3-503",
        IsAdministrator = false,
        IsEnabled = false,
        IsBuiltIn = true
    };

    // ------------------------------------------------ recommendation

    [Fact]
    public void An_existing_standard_account_is_recommended()
    {
        var plan = ChildAccountPlanner.Plan([Admin(), Standard()], "Lucas", ParentSid);

        Assert.Equal(ChildAccountAction.UseExisting, plan.Action);
        Assert.Equal("Barn", plan.SelectedAccount?.Username);
        Assert.Equal("Jimmy", plan.RecoveryAdministrator?.Username);
        Assert.True(plan.IsActionable);
    }

    [Fact]
    public void With_no_standard_account_one_would_be_created()
    {
        var plan = ChildAccountPlanner.Plan([Admin()], "Lucas", ParentSid);

        Assert.Equal(ChildAccountAction.CreateNew, plan.Action);
        Assert.Equal("Lucas", plan.ProposedUserName);
        Assert.True(plan.IsActionable);
    }

    [Fact]
    public void Built_in_accounts_are_never_candidates()
    {
        // DefaultAccount and the guest account are Windows' own, and using one
        // as a child account causes problems nobody expects.
        var plan = ChildAccountPlanner.Plan([Admin(), BuiltIn()], "Lucas", ParentSid);

        Assert.Equal(ChildAccountAction.CreateNew, plan.Action);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public void A_disabled_account_is_not_a_candidate()
    {
        var plan = ChildAccountPlanner.Plan(
            [Admin(), Standard("Gammal", "S-1-5-21-1-2-3-1500", enabled: false)],
            "Lucas",
            ParentSid);

        Assert.Equal(ChildAccountAction.CreateNew, plan.Action);
    }

    // ------------------------------------------------ the recovery invariant

    [Fact]
    public void No_enabled_administrator_blocks_everything()
    {
        // Nothing may proceed without a way back in.
        var plan = ChildAccountPlanner.Plan(
            [Admin(enabled: false), Standard()],
            "Lucas",
            ParentSid);

        Assert.Equal(ChildAccountAction.Blocked, plan.Action);
        Assert.Contains(ChildAccountBlocker.NoRecoveryAdministrator, plan.Blockers);
        Assert.False(plan.IsActionable);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Choosing_the_only_administrator_as_the_child_is_blocked()
    {
        // This is the single most direct route to locking a parent out.
        var plan = ChildAccountPlanner.Plan(
            [Admin()],
            "Lucas",
            currentUserSid: ParentSid,
            chosenSid: ParentSid);

        Assert.Equal(ChildAccountAction.Blocked, plan.Action);
        Assert.Contains(ChildAccountBlocker.ChosenAccountIsOnlyAdministrator, plan.Blockers);
    }

    [Fact]
    public void Choosing_the_signed_in_parents_own_account_is_blocked()
    {
        var plan = ChildAccountPlanner.Plan(
            [Admin(), Admin("Reserv", "S-1-5-21-1-2-3-1009"), Standard()],
            "Lucas",
            currentUserSid: ParentSid,
            chosenSid: ParentSid);

        Assert.Equal(ChildAccountAction.Blocked, plan.Action);
        Assert.Contains(ChildAccountBlocker.ChosenAccountIsCurrentUser, plan.Blockers);
    }

    [Fact]
    public void A_second_administrator_makes_demoting_the_first_possible()
    {
        // With a spare administrator there is still a way back in, so demoting
        // one is a real option rather than a trap.
        var plan = ChildAccountPlanner.Plan(
            [Admin(), Admin("Reserv", "S-1-5-21-1-2-3-1009")],
            "Lucas",
            currentUserSid: "S-1-5-21-1-2-3-1009",
            chosenSid: ParentSid);

        Assert.Equal(ChildAccountAction.DemoteExisting, plan.Action);
        Assert.Equal("Reserv", plan.RecoveryAdministrator?.Username);
    }

    [Fact]
    public void Unreadable_accounts_block_rather_than_guess()
    {
        var plan = ChildAccountPlanner.Plan([], "Lucas");

        Assert.Equal(ChildAccountAction.Blocked, plan.Action);
        Assert.Contains(ChildAccountBlocker.AccountsUnreadable, plan.Blockers);
    }

    [Fact]
    public void A_chosen_disabled_account_is_blocked()
    {
        var plan = ChildAccountPlanner.Plan(
            [Admin(), Standard("Gammal", "S-1-5-21-1-2-3-1500", enabled: false)],
            "Lucas",
            ParentSid,
            chosenSid: "S-1-5-21-1-2-3-1500");

        Assert.Contains(ChildAccountBlocker.ChosenAccountDisabled, plan.Blockers);
    }

    [Fact]
    public void A_chosen_built_in_account_is_blocked()
    {
        var plan = ChildAccountPlanner.Plan(
            [Admin(), BuiltIn()],
            "Lucas",
            ParentSid,
            chosenSid: "S-1-5-21-1-2-3-503");

        Assert.Contains(ChildAccountBlocker.ChosenAccountIsBuiltIn, plan.Blockers);
    }

    // ------------------------------------------------ user names

    [Theory]
    [InlineData("Lucas", "Lucas")]
    [InlineData("  Nora  ", "Nora")]
    [InlineData("Åsa", "Åsa")]
    [InlineData("", "Barn")]
    [InlineData("   ", "Barn")]
    public void A_child_name_becomes_a_sensible_account_name(string childName, string expected) =>
        Assert.Equal(expected, ChildAccountPlanner.ProposeUserName(childName));

    [Fact]
    public void Characters_Windows_forbids_are_stripped_not_rejected()
    {
        // The parent named their child, not a Windows account.
        var name = ChildAccountPlanner.ProposeUserName(@"Lu/ca\s:*?");

        Assert.Equal("Lucas", name);
    }

    [Fact]
    public void An_over_long_name_is_truncated_to_the_windows_limit()
    {
        var name = ChildAccountPlanner.ProposeUserName(new string('a', 40));

        Assert.Equal(20, name.Length);
    }

    [Fact]
    public void A_name_that_is_only_forbidden_characters_falls_back()
    {
        Assert.Equal("Barn", ChildAccountPlanner.ProposeUserName(@"\\//::"));
    }

    [Fact]
    public void A_proposed_name_never_ends_in_a_period()
    {
        // Windows rejects account names ending in a period.
        Assert.DoesNotContain(".", ChildAccountPlanner.ProposeUserName("Lucas.")[^1..], StringComparison.Ordinal);
    }

    // ------------------------------------------------ steps and wording

    [Fact]
    public void A_plan_ends_by_confirming_the_parent_can_still_log_in()
    {
        var plan = ChildAccountPlanner.Plan([Admin(), Standard()], "Lucas", ParentSid);

        Assert.Contains(plan.Steps, s => s.Contains("logga in som", StringComparison.Ordinal));
    }

    [Fact]
    public void Creating_an_account_verifies_it_is_not_an_administrator()
    {
        var plan = ChildAccountPlanner.Plan([Admin()], "Lucas", ParentSid);

        Assert.Contains(plan.Steps, s => s.Contains("inte är administratör", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ChildAccountBlocker.NoRecoveryAdministrator)]
    [InlineData(ChildAccountBlocker.ChosenAccountIsOnlyAdministrator)]
    [InlineData(ChildAccountBlocker.ChosenAccountIsCurrentUser)]
    [InlineData(ChildAccountBlocker.AccountsUnreadable)]
    [InlineData(ChildAccountBlocker.ChosenAccountDisabled)]
    [InlineData(ChildAccountBlocker.ChosenAccountIsBuiltIn)]
    public void Every_blocker_has_plain_language_wording(ChildAccountBlocker blocker)
    {
        var text = ChildAccountPlanner.Describe(blocker);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("0x", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SID", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_planner_cannot_change_an_account()
    {
        // Account management would need a new, visibly named type to exist at
        // all. The planner produces plans.
        var methods = typeof(ChildAccountPlanner).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var forbidden in new[] { "Create", "Delete", "Apply", "Set", "Enable", "Disable", "Demote", "Add", "Remove" })
        {
            Assert.DoesNotContain(methods, m => m.Name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }
}
