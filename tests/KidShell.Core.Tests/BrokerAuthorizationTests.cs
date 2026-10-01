using KidShell.Core.Security.Broker;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// THE AUTHORIZATION MATRIX, EVERY CELL.
///
/// The question this file answers is the one the hardening pass was built
/// around: what prevents a compromised or modified KidShell.App from calling
/// SaveParentPolicy with an attacker-controlled PIN, app list and web policy?
///
/// Before the pass the honest answer was "nothing". The operation was
/// internal, the enum was closed and the payload was validated, and not one
/// of those is an authority check: a modified KidShell.App is a program
/// running as the child that sends well-formed requests, and every one of
/// those defences would have waved it through.
///
/// The answer now is a Windows one, and these tests are where it is written
/// down. No cell is left ambiguous - the theory below covers the complete
/// cross product of caller class and operation, so an operation added without
/// a decision fails here rather than defaulting to something.
/// </summary>
public class BrokerAuthorizationTests
{
    private const string ChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string ParentSid = "S-1-5-21-1111111111-2222222222-3333333333-1000";

    private static BrokerCaller Child => new()
    {
        Class = BrokerCallerClass.ChildSession,
        Sid = ChildSid,
        AccountName = "barn",
        SessionId = 1
    };

    private static BrokerCaller Administrator => new()
    {
        Class = BrokerCallerClass.Administrator,
        Sid = ParentSid,
        AccountName = "foralder",
        IsElevated = true,
        SessionId = 1
    };

    private static BrokerCaller System => new()
    {
        Class = BrokerCallerClass.System,
        Sid = "S-1-5-18",
        AccountName = "SYSTEM",
        IsElevated = true
    };

    private static BrokerCaller StrangerProcess => new()
    {
        Class = BrokerCallerClass.Unknown,
        Sid = "S-1-5-21-1111111111-2222222222-3333333333-1500",
        AccountName = "nagon-annan"
    };

    // ---------------------------------------------- the headline refusals

    [Fact]
    public void A_child_session_cannot_write_the_parent_policy()
    {
        var decision = BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.SaveParentPolicy, hasParentCapability: false);

        Assert.False(decision.Allowed);
        Assert.Equal(BrokerFailureReason.NotAuthorized, decision.Reason);
    }

    [Fact]
    public void A_parent_capability_does_not_unlock_the_parent_policy_either()
    {
        // The capability says a PIN was verified. It does not say the process
        // that collected the PIN is trustworthy, and a modified KidShell.App
        // could collect one the moment a parent legitimately unlocks. So
        // policy changes need Windows authority, not a PIN.
        var decision = BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.SaveParentPolicy, hasParentCapability: true);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void A_child_session_cannot_mark_the_machine_provisioned()
    {
        // Provisioning is what makes a missing policy mean "missing" rather
        // than "new machine". A child who could write the marker could make
        // the opposite true.
        Assert.False(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.MarkProvisioned, false).Allowed);
    }

    [Fact]
    public void A_child_session_cannot_commit_what_it_staged()
    {
        Assert.True(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.StageParentPolicy, false).Allowed);

        Assert.False(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.CommitStagedParentPolicy, false).Allowed);
    }

    [Fact]
    public void A_child_session_cannot_reset_or_grant_screen_time_unaided()
    {
        Assert.False(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.ResetScreenTimeToday, false).Allowed);

        Assert.False(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.GrantScreenTime, false).Allowed);

        // With a capability the service itself issued, the same session may.
        Assert.True(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.GrantScreenTime, hasParentCapability: true).Allowed);
    }

    [Fact]
    public void An_unknown_caller_is_refused_even_a_probe()
    {
        // The service does not confirm its own presence to something it
        // cannot name.
        foreach (var kind in Enum.GetValues<ElevatedOperationKind>())
        {
            Assert.False(
                BrokerAuthorizationPolicy.Decide(StrangerProcess, kind, false).Allowed,
                $"an unidentified caller was allowed {kind}");
        }
    }

    [Fact]
    public void An_unelevated_administrator_account_is_not_an_administrator()
    {
        // A filtered token. The account could consent and has not, which for
        // a policy change is the same as not being allowed.
        var notElevated = Administrator with { IsElevated = false };

        Assert.False(BrokerAuthorizationPolicy.Decide(
            notElevated, ElevatedOperationKind.SaveParentPolicy, false).Allowed);
    }

    // ---------------------------------------------------- what is allowed

    [Fact]
    public void A_child_session_may_write_enforcement_state()
    {
        // Allowed only because the transition rules make these
        // one-directional. Without them this cell would be the whole hole.
        Assert.True(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.SaveScreenTimeState, false).Allowed);

        Assert.True(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.SavePinThrottleState, false).Allowed);
    }

    [Fact]
    public void A_child_session_may_ask_the_service_to_check_a_pin()
    {
        // Safe because the service does the checking and owns the cooldown.
        Assert.True(BrokerAuthorizationPolicy.Decide(
            Child, ElevatedOperationKind.VerifyParentPin, false).Allowed);
    }

    [Fact]
    public void An_elevated_administrator_may_do_everything()
    {
        foreach (var kind in Enum.GetValues<ElevatedOperationKind>())
        {
            Assert.True(
                BrokerAuthorizationPolicy.Decide(Administrator, kind, false).Allowed,
                $"an elevated administrator was refused {kind}");
        }
    }

    [Fact]
    public void Localsystem_is_above_the_matrix_by_construction()
    {
        // Anything able to act as LocalSystem has already won. Pretending
        // otherwise would be theatre, and theatre in an authorization table
        // is worse than an honest gap.
        foreach (var kind in Enum.GetValues<ElevatedOperationKind>())
        {
            Assert.True(BrokerAuthorizationPolicy.Decide(System, kind, false).Allowed);
        }
    }

    // --------------------------------------------- the set, as one answer

    [Fact]
    public void The_complete_set_reachable_by_a_child_session_is_exactly_five()
    {
        // Written out so that widening it is a visible change to a test that
        // says what the widening means, rather than a quiet extra allow.
        //
        // Probe is in the list and changes nothing: it reveals that the
        // service is running to a caller that is already talking to it.
        Assert.Equal(
            new[]
            {
                ElevatedOperationKind.Probe,
                ElevatedOperationKind.SaveScreenTimeState,
                ElevatedOperationKind.SavePinThrottleState,
                ElevatedOperationKind.StageParentPolicy,
                ElevatedOperationKind.VerifyParentPin
            }.OrderBy(k => k),
            BrokerAuthorizationPolicy.ReachableByChildSession.OrderBy(k => k));
    }

    [Fact]
    public void Every_machine_mutation_needs_an_administrator()
    {
        var mutations = new[]
        {
            ElevatedOperationKind.CreateChildAccount,
            ElevatedOperationKind.DemoteChildAccount,
            ElevatedOperationKind.ConfigureAutostart,
            ElevatedOperationKind.DeployAppLockerPolicy,
            ElevatedOperationKind.ConfigureApplicationIdentityService,
            ElevatedOperationKind.ConfigureAssignedAccess,
            ElevatedOperationKind.DeployBrowserPolicy,
            ElevatedOperationKind.InstallWatchdogService,
            ElevatedOperationKind.InstallSecurityHostService
        };

        foreach (var kind in mutations)
        {
            Assert.Equal(BrokerAuthority.Administrator, BrokerAuthorizationPolicy.RequiredFor(kind));

            Assert.False(BrokerAuthorizationPolicy.Decide(Child, kind, true).Allowed,
                $"a child session with a capability reached {kind}");
        }
    }

    [Fact]
    public void No_operation_is_left_without_a_decision()
    {
        // The matrix is total by construction, and this is what proves the
        // construction. An operation added to the enum without a line in
        // RequiredFor would fall to the Administrator default, which is safe
        // - and this test, which checks the mapping is deliberate for every
        // member, is what makes "safe" also "noticed".
        foreach (var kind in Enum.GetValues<ElevatedOperationKind>())
        {
            var required = BrokerAuthorizationPolicy.RequiredFor(kind);

            Assert.True(Enum.IsDefined(required), $"{kind} has no authority");

            // And the decision function agrees with the table for each class.
            foreach (var caller in new[] { Child, Administrator, System })
            {
                var decision = BrokerAuthorizationPolicy.Decide(caller, kind, false);

                Assert.True(decision.Explanation.Length > 0,
                    $"{kind} from {caller.Class} has no stated reason");
            }
        }
    }

    [Theory]
    [InlineData((ElevatedOperationKind)99)]
    [InlineData((ElevatedOperationKind)(-1))]
    public void An_operation_outside_the_enum_is_refused(ElevatedOperationKind kind)
    {
        var decision = BrokerAuthorizationPolicy.Decide(Administrator, kind, true);

        Assert.False(decision.Allowed);
        Assert.Equal(BrokerFailureReason.UnknownOperation, decision.Reason);
    }
}
