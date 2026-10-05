using System.Text.RegularExpressions;
using KidShell.Core.Security.AppControl;
using KidShell.Core.Security.Validation;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// WINDOWS LOCALIZES ITS BUILT-IN GROUP NAMES. THE VALIDATION TOOLSET DID NOT
/// KNOW THAT.
///
/// On the Swedish machine used for physical validation, Administrators is
/// "Administratörer" and Users is "Användare". `Get-LocalGroup -Name
/// 'Administrators'` throws GroupNotFoundException there, and three scripts
/// asked for groups by English name:
///
///   apply\01-create-child-account.ps1   refused to run, because no enabled
///                                       administrator could be found in a
///                                       group that did not exist.
///   03-verify-accounts.ps1              reported the child as being in no
///                                       privileged group, because no
///                                       privileged group could be found.
///   tools\install\Test-KidShellInstallation.ps1
///                                       reported "no ordinary account has
///                                       write access to the install root"
///                                       whatever the access list said.
///
/// The first fails closed. The third fails OPEN, and that is the one that
/// matters: a check whose entire purpose is catching a privilege escalation
/// reported that there was none.
///
/// These tests hold the line that identity is a SID. They deliberately assert
/// on the ABSENCE of English group names in the decision surface as well as on
/// the presence of the right SIDs, because a fix that adds Swedish names would
/// pass every positive test and be wrong on the next machine.
/// </summary>
public class LocalizedSecurityGroupTests
{
    // ------------------------------------------------- proof 1 and proof 2
    //
    // The two groups the reported defect named, resolved by well-known SID.

    [Fact]
    public void Administrators_resolves_by_its_well_known_sid()
    {
        Assert.Equal("S-1-5-32-544", WellKnownSecurityGroups.AdministratorsSid);
        Assert.Equal("S-1-5-32-544", WellKnownSecurityGroups.SidOf("Administrators"));
        Assert.Equal("Administrators", WellKnownSecurityGroups.LabelOf("S-1-5-32-544"));
    }

    [Fact]
    public void Users_resolves_by_its_well_known_sid()
    {
        Assert.Equal("S-1-5-32-545", WellKnownSecurityGroups.UsersSid);
        Assert.Equal("S-1-5-32-545", WellKnownSecurityGroups.SidOf("Users"));
        Assert.Equal("Users", WellKnownSecurityGroups.LabelOf("S-1-5-32-545"));
    }

    // --------------------------------------------------------------- proof 3
    //
    // Parent administrator detection must not depend on the string
    // "Administrators". The table is the only thing the scripts consult, so the
    // test that means something is: does the identity survive not knowing the
    // name?

    [Fact]
    public void An_administrator_is_identified_without_the_word_Administrators()
    {
        // Everything the decision needs, with the label discarded.
        var sid = WellKnownSecurityGroups.AdministratorsSid;

        Assert.True(WellKnownSecurityGroups.IsPrivileged(sid));
        Assert.DoesNotContain("Administrators", sid, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Administratörer")]   // Swedish
    [InlineData("Administratoren")]   // German
    [InlineData("Administrateurs")]   // French
    [InlineData("Administrators")]    // English, which is also just a name
    public void A_display_name_is_never_accepted_as_an_identity(string displayName)
    {
        // The guard that catches a localized name arriving where a SID belongs.
        Assert.False(WellKnownSecurityGroups.LooksLikeSid(displayName));
        Assert.False(WellKnownSecurityGroups.IsPrivileged(displayName));
        Assert.False(WellKnownSecurityGroups.IsOrdinaryAccount(displayName));
    }

    // --------------------------------------------------------------- proof 4
    //
    // Child privileged-group detection must not depend on English names, and it
    // must cover every privileged group the old English array listed.

    [Fact]
    public void Every_privileged_group_the_old_english_array_listed_is_covered_by_sid()
    {
        // The exact six names 03-verify-accounts.ps1 used to hard-code, with the
        // SIDs they should always have meant.
        var replaced = new Dictionary<string, string>
        {
            ["Administrators"] = "S-1-5-32-544",
            ["Power Users"] = "S-1-5-32-547",
            ["Backup Operators"] = "S-1-5-32-551",
            ["Remote Desktop Users"] = "S-1-5-32-555",
            ["Remote Management Users"] = "S-1-5-32-580",
            ["Hyper-V Administrators"] = "S-1-5-32-578"
        };

        foreach (var (label, sid) in replaced)
        {
            Assert.Equal(sid, WellKnownSecurityGroups.SidOf(label));
            Assert.True(WellKnownSecurityGroups.IsPrivileged(sid),
                $"{label} ({sid}) must still count as privileged.");
        }
    }

    [Fact]
    public void Privileged_detection_is_expressed_entirely_in_sids()
    {
        Assert.NotEmpty(WellKnownSecurityGroups.Privileged);

        foreach (var group in WellKnownSecurityGroups.Privileged)
        {
            Assert.True(WellKnownSecurityGroups.LooksLikeSid(group.Sid),
                $"{group.Label} is identified by '{group.Sid}', which is not a SID.");
        }
    }

    [Fact]
    public void An_unprivileged_principal_is_not_reported_as_privileged()
    {
        Assert.False(WellKnownSecurityGroups.IsPrivileged(WellKnownSecurityGroups.UsersSid));
        Assert.False(WellKnownSecurityGroups.IsPrivileged("S-1-1-0"));

        // A child account's own SID. Not a group at all.
        Assert.False(WellKnownSecurityGroups.IsPrivileged(
            "S-1-5-21-1111111111-2222222222-3333333333-1001"));
    }

    // --------------------------------------------------------------- proof 5
    //
    // Adding the child to the standard users group must target S-1-5-32-545.

    [Fact]
    public void The_standard_users_group_is_targeted_by_sid()
    {
        var users = Assert.Single(WellKnownSecurityGroups.All, g => g.Sid == "S-1-5-32-545");

        Assert.Equal("Users", users.Label);
        Assert.True(users.OrdinaryAccount);

        // And it is NOT privileged - adding the child here must stay the
        // harmless half of account creation.
        Assert.False(users.Privileged);
    }

    [Fact]
    public void No_group_is_both_privileged_and_an_ordinary_account()
    {
        // The two sets drive opposite decisions: one must be empty for the
        // child, the other must be absent from an access list. A group in both
        // would make one of those checks nonsense.
        var overlap = WellKnownSecurityGroups.Privileged
            .Select(g => g.Sid)
            .Intersect(WellKnownSecurityGroups.OrdinaryAccounts.Select(g => g.Sid))
            .ToList();

        Assert.Empty(overlap);
    }

    // --------------------------------------------------------------- proof 6
    //
    // Evidence may carry canonical English labels. Authorization may not use
    // them.

    [Fact]
    public void Labels_are_canonical_english_so_two_machines_produce_one_document()
    {
        // A run on Swedish Windows and a run on English Windows must produce
        // comparable evidence, so the LABEL is fixed English even though the
        // machine's own name for the group is not.
        foreach (var group in WellKnownSecurityGroups.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Label));
            Assert.Matches(new Regex(@"^[A-Za-z][A-Za-z \-]*$"), group.Label);
        }
    }

    [Fact]
    public void A_label_is_not_an_identity_even_when_it_is_the_right_label()
    {
        // The asymmetry is the point. SidOf("Administrators") is allowed,
        // because it reads this product's own canonical label out of its own
        // table. IsPrivileged("Administrators") is not, because that would be
        // an authorization decision made from a name.
        Assert.NotNull(WellKnownSecurityGroups.SidOf("Administrators"));
        Assert.False(WellKnownSecurityGroups.IsPrivileged("Administrators"));
    }

    [Fact]
    public void An_unknown_sid_is_returned_rather_than_guessed_at()
    {
        const string unknown = "S-1-5-32-9999";

        Assert.Equal(unknown, WellKnownSecurityGroups.LabelOf(unknown));
        Assert.False(WellKnownSecurityGroups.IsPrivileged(unknown));
        Assert.False(WellKnownSecurityGroups.IsOrdinaryAccount(unknown));
        Assert.Null(WellKnownSecurityGroups.SidOf("Domain Admins"));
    }

    // -------------------------------------------------- the fail-open check
    //
    // The install verifier's permission check. The identities it looks for are
    // the ones that mean "anybody with an account on this machine".

    [Fact]
    public void The_ordinary_account_set_covers_what_the_old_regex_tried_to_match()
    {
        // The regex was 'Users|Everyone|Authenticated'. These are the SIDs it
        // was reaching for, and could never match on a localized machine.
        var sids = WellKnownSecurityGroups.OrdinaryAccounts.Select(g => g.Sid).ToList();

        Assert.Contains("S-1-5-32-545", sids);   // Users
        Assert.Contains("S-1-1-0", sids);        // Everyone
        Assert.Contains("S-1-5-11", sids);       // Authenticated Users
        Assert.Contains("S-1-5-4", sids);        // Interactive
    }

    [Fact]
    public void LocalSystem_is_not_an_ordinary_account()
    {
        // The install root is SUPPOSED to be writable by LocalSystem and by
        // administrators. A check that flagged those would be noise, and noise
        // is how a real finding gets scrolled past.
        Assert.False(WellKnownSecurityGroups.IsOrdinaryAccount(WellKnownSecurityGroups.SystemSid));
        Assert.False(WellKnownSecurityGroups.IsOrdinaryAccount(WellKnownSecurityGroups.AdministratorsSid));
    }

    // ------------------------------------------------------ one definition

    [Fact]
    public void Every_sid_in_the_table_is_well_formed_and_unique()
    {
        foreach (var group in WellKnownSecurityGroups.All)
        {
            Assert.True(WellKnownSecurityGroups.LooksLikeSid(group.Sid),
                $"{group.Label} is '{group.Sid}'.");
        }

        Assert.Equal(
            WellKnownSecurityGroups.All.Count,
            WellKnownSecurityGroups.All.Select(g => g.Sid).Distinct().Count());

        Assert.Equal(
            WellKnownSecurityGroups.All.Count,
            WellKnownSecurityGroups.All.Select(g => g.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_applocker_rules_take_their_sids_from_the_same_table()
    {
        // These used to be separate literals in AppControlValidator,
        // AppLockerPolicyWriter and AppLockerOperations. Four copies of a
        // security identity is three chances to be wrong in a place nobody
        // looks.
        Assert.Equal(WellKnownSecurityGroups.AdministratorsSid, WellKnownSids.Administrators);
        Assert.Equal(WellKnownSecurityGroups.SidOf("Everyone"), WellKnownSids.Everyone);
        Assert.Equal(WellKnownSids.Everyone, AppLockerPolicyWriter.EveryoneSid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    [InlineData("S-1")]
    [InlineData("S-1-5-32-")]
    [InlineData("S-1-5-32-abc")]
    public void Rubbish_is_not_mistaken_for_an_identity(string? value)
    {
        Assert.False(WellKnownSecurityGroups.LooksLikeSid(value));
        Assert.False(WellKnownSecurityGroups.IsPrivileged(value));
        Assert.False(WellKnownSecurityGroups.IsOrdinaryAccount(value));
        Assert.Null(WellKnownSecurityGroups.SidOf(value));
    }
}
