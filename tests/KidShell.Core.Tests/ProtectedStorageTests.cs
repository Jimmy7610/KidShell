using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>A store a test can put into any state it likes.</summary>
internal sealed class FakeProtectedPolicyStore(ProtectedStoreState state) : IProtectedPolicyStore
{
    private readonly Dictionary<string, string> _documents = [];

    public int WriteAttempts { get; private set; }

    public ProtectedStoreState Probe() => state;

    public string? Read(string name) => _documents.GetValueOrDefault(name);

    public bool Write(string name, string content)
    {
        WriteAttempts++;

        if (!state.IsTrustworthy)
        {
            return false;
        }

        _documents[name] = content;
        return true;
    }
}

/// <summary>
/// EXTERNAL AUDIT FINDING 03 — the trust boundary around security-critical
/// state.
///
/// NOTHING HERE TOUCHES THIS MACHINE. The plan is a description: a directory
/// and a list of access-control entries that an elevated operation would apply
/// on a dedicated device. These tests check the description, which is the part
/// that can be got wrong silently.
///
/// The claim being tested is narrow and worth stating plainly: a child with
/// full control of their own profile must not thereby have control of the
/// rules they are subject to. Integrity checks do not achieve that - an HMAC
/// whose key sits beside the data, readable by the same account, is
/// recomputable by whoever can edit the data. Only a boundary the child cannot
/// cross achieves it, and Windows already has one.
/// </summary>
public class ProtectedStorageTests
{
    private static ProtectedStorePlan Plan() => ProtectedStorePlan.For(@"C:\ProgramData");

    // ------------------------------------------------ the plan

    [Fact]
    public void The_child_cannot_write_to_the_protected_store()
    {
        var plan = Plan();

        Assert.True(plan.IsChildWriteProtected);

        var child = plan.Entries.Single(e => e.Principal == ProtectedStorePrincipal.Child);

        Assert.Equal(ProtectedStoreRights.ReadOnly, child.Rights);
        Assert.False(child.Rights.HasFlag(ProtectedStoreRights.Write));
        Assert.False(child.Rights.HasFlag(ProtectedStoreRights.Delete));
        Assert.False(child.Rights.HasFlag(ProtectedStoreRights.ChangePermissions));
    }

    /// <summary>
    /// KidShell runs as the child and has to load the policy it is enforcing,
    /// so read access is required rather than merely tolerated.
    /// </summary>
    [Fact]
    public void The_child_can_still_read_the_policy_being_enforced() =>
        Assert.True(Plan().IsChildReadable);

    /// <summary>
    /// The other way to get this wrong: lock it down so hard the parent cannot
    /// fix it either. A family with an unrecoverable policy has a broken
    /// computer.
    /// </summary>
    [Fact]
    public void The_parent_can_still_change_the_policy() =>
        Assert.True(Plan().IsAdministratorRecoverable);

    [Fact]
    public void Nobody_else_on_the_machine_is_granted_anything() =>
        Assert.DoesNotContain(Plan().Entries, e => e.Principal == ProtectedStorePrincipal.Users);

    /// <summary>
    /// ProgramData grants CREATOR OWNER full control of what is created in it.
    /// A store that kept inherited permissions would therefore be fully
    /// controlled by whoever wrote it - which on a child's machine could be
    /// the child, which is the entire problem again.
    /// </summary>
    [Fact]
    public void Inherited_permissions_are_removed() =>
        Assert.True(Plan().RemoveInheritance);

    [Fact]
    public void The_store_is_machine_wide_not_inside_a_user_profile()
    {
        var directory = Plan().Directory;

        Assert.StartsWith(@"C:\ProgramData", directory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\Users\", directory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", directory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The plan is about a Windows machine whatever host computed it, so the
    /// separators must not follow the host's.
    /// </summary>
    [Fact]
    public void The_planned_path_uses_Windows_separators()
    {
        var directory = Plan().Directory;

        Assert.Contains('\\', directory);
        Assert.DoesNotContain('/', directory);
    }

    // ------------------------------------------------ no silent fallback

    [Theory]
    [InlineData(ProtectedStoreStatus.NotProvisioned)]
    [InlineData(ProtectedStoreStatus.PermissionsWrong)]
    [InlineData(ProtectedStoreStatus.Unavailable)]
    [InlineData(ProtectedStoreStatus.DevelopmentOnly)]
    public void A_production_build_never_falls_back_to_child_writable_storage(ProtectedStoreStatus status)
    {
        var state = new ProtectedStoreState(status, "test");

        Assert.False(ProtectedStoreGate.MayUseUnprotectedStorage(state, isDevelopmentBuild: false));
    }

    [Theory]
    [InlineData(ProtectedStoreStatus.NotProvisioned)]
    [InlineData(ProtectedStoreStatus.PermissionsWrong)]
    [InlineData(ProtectedStoreStatus.Unavailable)]
    public void A_development_build_may_fall_back_and_says_so(ProtectedStoreStatus status)
    {
        var logger = new RecordingLogger();
        var state = new ProtectedStoreState(status, "test");

        Assert.True(ProtectedStoreGate.MayUseUnprotectedStorage(state, isDevelopmentBuild: true, logger));
        Assert.Contains(logger.Messages, m => m.Contains("development build", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_production_build_says_loudly_why_it_refused()
    {
        var logger = new RecordingLogger();
        var state = new ProtectedStoreState(ProtectedStoreStatus.PermissionsWrong, "child can write");

        ProtectedStoreGate.MayUseUnprotectedStorage(state, isDevelopmentBuild: false, logger);

        Assert.True(logger.HasError);
        Assert.Contains(logger.Messages, m => m.Contains("will not fall back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_working_store_is_never_bypassed()
    {
        var ready = new ProtectedStoreState(ProtectedStoreStatus.Ready, string.Empty);

        Assert.False(ProtectedStoreGate.MayUseUnprotectedStorage(ready, isDevelopmentBuild: true));
        Assert.False(ProtectedStoreGate.MayUseUnprotectedStorage(ready, isDevelopmentBuild: false));
    }

    /// <summary>
    /// Wrong permissions are worse than none: the store looks like protection
    /// and is not, so a parent would be told they were safe.
    /// </summary>
    [Fact]
    public void Wrong_permissions_are_not_treated_as_trustworthy() =>
        Assert.False(new ProtectedStoreState(ProtectedStoreStatus.PermissionsWrong, "").IsTrustworthy);

    [Fact]
    public void A_development_store_is_never_treated_as_trustworthy() =>
        Assert.False(new ProtectedStoreState(ProtectedStoreStatus.DevelopmentOnly, "").IsTrustworthy);

    [Fact]
    public void An_untrustworthy_store_refuses_writes_rather_than_pretending()
    {
        var store = new FakeProtectedPolicyStore(
            new ProtectedStoreState(ProtectedStoreStatus.PermissionsWrong, "child can write"));

        Assert.False(store.Write("policy.json", "{}"));
        Assert.Null(store.Read("policy.json"));
        Assert.Equal(1, store.WriteAttempts);
    }

    // ------------------------------------------------ the classification

    [Fact]
    public void Everything_a_child_could_gain_by_editing_is_protected()
    {
        foreach (var item in PolicyDataClassification.Protected)
        {
            Assert.NotEqual(PolicyDataClass.ChildPersonalisation, item.Class);
            Assert.False(string.IsNullOrWhiteSpace(item.Consequence),
                $"{item.Name} is protected but nobody wrote down what it would cost");
        }
    }

    [Fact]
    public void The_rules_a_child_is_subject_to_are_all_on_the_protected_side()
    {
        var protectedNames = PolicyDataClassification.Protected.Select(i => i.Name).ToList();

        // The four the audit named, plus the security mode.
        Assert.Contains("parentPin.hash", protectedNames);
        Assert.Contains("apps", protectedNames);
        Assert.Contains("web.allowedDomains", protectedNames);
        Assert.Contains("screenTime.weekdayMinutes", protectedNames);
        Assert.Contains("screenTimeState.usedSeconds", protectedNames);
        Assert.Contains("securityMode", protectedNames);
    }

    /// <summary>
    /// The other half of the design: not protecting what does not need it. A
    /// UAC prompt to change an avatar is how a product teaches a family to
    /// click through UAC prompts.
    /// </summary>
    [Fact]
    public void A_childs_own_choices_stay_where_the_child_can_change_them()
    {
        var childWritable = PolicyDataClassification.ChildWritable.Select(i => i.Name).ToList();

        Assert.Contains("child.avatarId", childWritable);
        Assert.Contains("child.themeId", childWritable);
        Assert.Contains("child.name", childWritable);
    }

    [Fact]
    public void Every_item_is_classified_exactly_once()
    {
        var names = PolicyDataClassification.Items.Select(i => i.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotEmpty(names);
    }

    [Fact]
    public void The_parent_pin_is_classified_as_a_secret()
    {
        var pin = PolicyDataClassification.Items.Single(i => i.Name == "parentPin.hash");

        Assert.Equal(PolicyDataClass.Secret, pin.Class);
    }
}
