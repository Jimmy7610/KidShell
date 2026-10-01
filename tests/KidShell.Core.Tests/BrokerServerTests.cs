using System.Text.Json;
using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The privileged broker, as a decision-maker.
///
/// Everything here goes through <see cref="ElevatedBrokerServer.Handle(string, BrokerCaller)"/>
/// - the same entry point the pipe calls, with the same caller the pipe would
/// have resolved. The pipe itself is a thin shell around this; its own tests
/// cover the access list and the token, and nothing else about the broker
/// needs a pipe to be wrong.
/// </summary>
public class BrokerServerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 19, 0, 0, TimeSpan.Zero);

    private const string ChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string ParentSid = "S-1-5-21-1111111111-2222222222-3333333333-1000";

    private static BrokerCaller Child => new()
    {
        Class = BrokerCallerClass.ChildSession, Sid = ChildSid, AccountName = "barn", SessionId = 1
    };

    private static BrokerCaller Administrator => new()
    {
        Class = BrokerCallerClass.Administrator, Sid = ParentSid, AccountName = "foralder",
        IsElevated = true, SessionId = 1
    };

    private sealed record Fixture(
        ElevatedBrokerServer Server,
        InMemoryPrivilegedStore Store,
        ParentCapabilityRegistry Capabilities,
        FakeTimeProvider Time,
        RecordingLogger Logger);

    private static Fixture Build(Action<InMemoryPrivilegedStore>? seed = null)
    {
        var store = new InMemoryPrivilegedStore();
        seed?.Invoke(store);

        var time = new FakeTimeProvider(Start);
        var capabilities = new ParentCapabilityRegistry(time);
        var logger = new RecordingLogger();

        return new Fixture(
            new ElevatedBrokerServer(store, capabilities, logger, time),
            store, capabilities, time, logger);
    }

    private static string Line(ElevatedRequest request) => ElevatedProtocol.Serialize(request);

    private static ElevatedRequest Request(ElevatedOperationKind kind) => new()
    {
        Kind = kind,
        RequestId = Guid.NewGuid().ToString("n"),
        DryRun = false
    };

    private static string PolicyWithPin(string pin, int iterations = PinHasher.DefaultIterations)
    {
        var (hash, salt) = PinHasher.Hash(pin, iterations);

        return JsonSerializer.Serialize(
            new ParentPolicyDocument
            {
                ParentPin = new ParentPinSettings { Hash = hash, Salt = salt, Iterations = iterations }
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string Counter(int used, int sequence, string day = "2026-09-30") =>
        JsonSerializer.Serialize(
            new ScreenTimeState { LocalDate = day, UsedSeconds = used, Sequence = sequence },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ------------------------------------------------------- the protocol

    [Fact]
    public void A_malformed_request_is_rejected()
    {
        var response = Build().Server.Handle("{ not json", Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.MalformedRequest, response.Reason);
    }

    [Fact]
    public void An_unknown_protocol_is_rejected()
    {
        var response = Build().Server.Handle(
            Line(Request(ElevatedOperationKind.Probe) with { ProtocolVersion = 99 }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.UnsupportedProtocol, response.Reason);
    }

    [Fact]
    public void An_operation_outside_the_enum_is_rejected()
    {
        // Not through the record - the point is a message arriving on the
        // wire with a number this build has no member for.
        var response = Build().Server.Handle(
            """{"protocolVersion":1,"kind":"Nonsense","requestId":"x"}""", Child);

        Assert.False(response.Success);
        Assert.True(response.Reason is BrokerFailureReason.MalformedRequest
                                    or BrokerFailureReason.UnknownOperation);
    }

    [Fact]
    public void An_oversized_request_is_rejected_before_it_is_parsed()
    {
        var fixture = Build();
        var huge = new string('x', BrokerEndpoint.MaxRequestBytes + 1);

        var response = fixture.Server.Handle(huge, Child);

        Assert.Equal(BrokerFailureReason.PayloadTooLarge, response.Reason);

        // And nothing reached the store, which is the part that matters for
        // a service running as LocalSystem.
        Assert.Equal(0, fixture.Store.WriteCount);
    }

    [Fact]
    public void An_oversized_payload_inside_a_well_formed_request_is_rejected()
    {
        var fixture = Build();

        var response = fixture.Server.Handle(Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
        {
            ProtectedPayload = "{\"a\":\"" + new string('x', ProtectedPayloadPolicy.MaxPayloadBytes) + "\"}"
        }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.PayloadInvalid, response.Reason);
        Assert.Equal(0, fixture.Store.WriteCount);
    }

    [Fact]
    public void A_request_id_a_caller_chose_does_not_reach_the_log_unbounded()
    {
        var fixture = Build();

        var response = fixture.Server.Handle(Line(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.Probe,
            RequestId = new string('z', 500)
        }), Child);

        Assert.False(response.Success);
        Assert.True(response.RequestId.Length <= 64);
    }

    [Fact]
    public void A_probe_from_an_identified_caller_succeeds()
    {
        var response = Build().Server.Handle(Line(Request(ElevatedOperationKind.Probe)), Child);

        Assert.True(response.Success);
    }

    [Fact]
    public void An_unknown_caller_is_refused_and_nothing_is_written()
    {
        var fixture = Build();

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
            {
                ProtectedPayload = Counter(0, 1)
            }),
            BrokerCaller.Unknown);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.NotAuthorized, response.Reason);
        Assert.Equal(0, fixture.Store.WriteCount);
    }

    [Fact]
    public void Nothing_the_service_knows_leaks_back_to_the_child()
    {
        // A failure reason and a Swedish sentence. No exception type, no
        // path, no indication of which documents exist.
        var fixture = Build(s => s.RefuseWrites = true);

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
            {
                ProtectedPayload = Counter(60, 1)
            }), Child);

        Assert.False(response.Success);
        Assert.Null(response.Detail);
        Assert.DoesNotContain("Exception", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", response.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------- enforcement writes

    [Fact]
    public void A_valid_screen_time_increment_is_accepted()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ScreenTimeState, Counter(600, 5)));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
            {
                ProtectedPayload = Counter(630, 6)
            }), Child);

        Assert.True(response.Success, response.Message);
        Assert.Contains("630", fixture.Store.Read(ProtectedDocument.ScreenTimeState)!);
    }

    [Fact]
    public void A_screen_time_decrement_is_rejected_and_the_old_value_stands()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ScreenTimeState, Counter(3600, 20)));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
            {
                ProtectedPayload = Counter(0, 21)
            }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.TransitionRejected, response.Reason);
        Assert.Contains("3600", fixture.Store.Read(ProtectedDocument.ScreenTimeState)!);
    }

    [Fact]
    public void A_sequence_rollback_is_rejected()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ScreenTimeState, Counter(600, 20)));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
            {
                ProtectedPayload = Counter(600, 2)
            }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.TransitionRejected, response.Reason);
    }

    [Fact]
    public void A_throttle_increment_is_accepted_and_a_reset_is_not()
    {
        var fixture = Build();

        var escalate = JsonSerializer.Serialize(
            new PinThrottleState { FailedAttemptCount = 4, CooldownUntilUtc = Start.AddSeconds(30) },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.True(fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SavePinThrottleState) with
            {
                ProtectedPayload = escalate
            }), Child).Success);

        var clear = JsonSerializer.Serialize(
            new PinThrottleState { FailedAttemptCount = 0, CooldownUntilUtc = null },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.SavePinThrottleState) with
            {
                ProtectedPayload = clear
            }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.TransitionRejected, response.Reason);
    }

    // ------------------------------------------------------- parent pin

    [Fact]
    public void A_correct_pin_is_answered_with_a_capability()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child);

        Assert.True(response.Success, response.Message);
        Assert.False(string.IsNullOrWhiteSpace(response.ParentCapability));
        Assert.True(fixture.Capabilities.IsValid(response.ParentCapability, Child));
    }

    [Fact]
    public void A_wrong_pin_is_counted_where_the_child_cannot_reach_the_count()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "000000" }),
            Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.PinIncorrect, response.Reason);

        // The service persisted the failure itself. The child was not asked
        // to, and could not have declined.
        var stored = ProtectedStateTransitionRules.Parse<PinThrottleState>(
            fixture.Store.Read(ProtectedDocument.PinThrottleState));

        Assert.NotNull(stored);
        Assert.Equal(1, stored.FailedAttemptCount);
    }

    [Fact]
    public void Enough_wrong_pins_earn_a_cooldown_the_child_cannot_clear()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            fixture.Server.Handle(
                Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "000000" }),
                Child);
        }

        var throttled = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child);

        Assert.False(throttled.Success);
        Assert.Equal(BrokerFailureReason.PinThrottled, throttled.Reason);
        Assert.True(throttled.RetryAfterSeconds > 0);

        // And the correct PIN works again once the cooldown has run out,
        // because a parent must never be locked out of their own computer.
        fixture.Time.Advance(PinAttemptPolicy.MaximumDelay + TimeSpan.FromSeconds(1));

        Assert.True(fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).Success);
    }

    [Fact]
    public void A_restarted_service_has_not_forgiven_anything()
    {
        var store = new InMemoryPrivilegedStore();
        store.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790"));

        var time = new FakeTimeProvider(Start);
        var logger = new RecordingLogger();

        var first = new ElevatedBrokerServer(store, new ParentCapabilityRegistry(time), logger, time);

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            first.Handle(
                Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "000000" }),
                Child);
        }

        // A child who can make a service restart must not get the attempts
        // back - the same hole the in-memory throttle had, one layer down.
        var second = new ElevatedBrokerServer(store, new ParentCapabilityRegistry(time), logger, time);

        var response = second.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.PinThrottled, response.Reason);
    }

    [Fact]
    public void No_configured_pin_is_not_a_pin_that_matches_anything()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, """{"schemaVersion":1}"""));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "000000" }),
            Child);

        Assert.False(response.Success);
    }

    [Fact]
    public void A_stored_iteration_count_outside_the_accepted_range_refuses_the_pin()
    {
        // The count comes off disk, so it is untrusted in both directions:
        // too low weakens the hash, too high is a way to make a LocalSystem
        // service burn a core per attempt.
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy,
            PolicyWithPin("135790").Replace("\"iterations\":210000", "\"iterations\":1")));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child);

        Assert.False(response.Success);
    }

    [Fact]
    public void The_pin_never_reaches_the_protected_store()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "246801" }),
            Child);

        var throttle = fixture.Store.Read(ProtectedDocument.PinThrottleState) ?? string.Empty;

        Assert.DoesNotContain("246801", throttle, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capability_is_bound_to_the_caller_it_was_issued_to()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        var issued = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).ParentCapability;

        var someoneElse = Child with
        {
            Sid = "S-1-5-21-1111111111-2222222222-3333333333-1500"
        };

        Assert.False(fixture.Capabilities.IsValid(issued, someoneElse));

        // And a parent who authenticated at this computer has not authorized
        // anything in another session under the same account.
        Assert.False(fixture.Capabilities.IsValid(issued, Child with { SessionId = 7 }));
    }

    [Fact]
    public void A_capability_expires()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        var issued = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).ParentCapability;

        fixture.Time.Advance(BrokerEndpoint.ParentCapabilityLifetime + TimeSpan.FromMinutes(1));

        Assert.False(fixture.Capabilities.IsValid(issued, Child));
    }

    // -------------------------------------------------- parent grants

    [Fact]
    public void A_grant_is_applied_by_the_service_rather_than_supplied_by_the_caller()
    {
        var fixture = Build(s =>
        {
            s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790"));
            s.Seed(ProtectedDocument.ScreenTimeState, Counter(3600, 20));
        });

        var capability = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).ParentCapability;

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.GrantScreenTime) with
            {
                GrantMinutes = 15,
                ParentCapability = capability
            }), Child);

        Assert.True(response.Success, response.Message);

        var after = ProtectedStateTransitionRules.Parse<ScreenTimeState>(
            fixture.Store.Read(ProtectedDocument.ScreenTimeState))!;

        Assert.Equal(15, after.BonusMinutes);

        // The used counter is untouched: a grant adds allowance, it does not
        // erase time already spent.
        Assert.Equal(3600, after.UsedSeconds);
    }

    [Fact]
    public void A_reset_lowers_the_counter_and_keeps_the_clock_evidence()
    {
        var fixture = Build(s =>
        {
            s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790"));

            s.Seed(ProtectedDocument.ScreenTimeState, JsonSerializer.Serialize(
                new ScreenTimeState
                {
                    LocalDate = "2026-09-30", UsedSeconds = 3600, Sequence = 20, SuspiciousClockEvents = 3
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        });

        var capability = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).ParentCapability;

        Assert.True(fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.ResetScreenTimeToday) with
            {
                ParentCapability = capability
            }), Child).Success);

        var after = ProtectedStateTransitionRules.Parse<ScreenTimeState>(
            fixture.Store.Read(ProtectedDocument.ScreenTimeState))!;

        Assert.Equal(0, after.UsedSeconds);
        Assert.Equal(3, after.SuspiciousClockEvents);
    }

    [Fact]
    public void A_made_up_capability_does_not_work()
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ScreenTimeState, Counter(3600, 20)));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.ResetScreenTimeToday) with
            {
                ParentCapability = new string('a', 64)
            }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.ParentAuthorizationRequired, response.Reason);
        Assert.Contains("3600", fixture.Store.Read(ProtectedDocument.ScreenTimeState)!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(100_000)]
    public void A_grant_outside_the_allowed_range_is_refused(int minutes)
    {
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        var capability = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).ParentCapability;

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.GrantScreenTime) with
            {
                GrantMinutes = minutes,
                ParentCapability = capability
            }), Child);

        Assert.False(response.Success);
    }

    // ------------------------------------------------- staging and commit

    [Fact]
    public void A_child_may_stage_a_policy_and_the_live_one_does_not_change()
    {
        var live = PolicyWithPin("135790");
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, live));

        var hostile = PolicyWithPin("000000");

        var staged = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with { ProtectedPayload = hostile }),
            Child);

        Assert.True(staged.Success);
        Assert.Equal(ElevatedBrokerServer.Digest(hostile), staged.StagedDigest);

        // The live policy is byte for byte what it was, and the proposal is
        // sitting in the slot nothing enforcing reads.
        Assert.Equal(live, fixture.Store.Read(ProtectedDocument.ParentPolicy));
        Assert.Equal(hostile, fixture.Store.ReadStaged());
    }

    [Fact]
    public void A_child_cannot_commit_what_it_staged()
    {
        var fixture = Build();

        fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with
            {
                ProtectedPayload = PolicyWithPin("000000")
            }), Child);

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = ElevatedBrokerServer.Digest(PolicyWithPin("000000"))
            }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.NotAuthorized, response.Reason);
        Assert.Null(fixture.Store.Read(ProtectedDocument.ParentPolicy));
    }

    [Fact]
    public void An_administrator_commits_what_was_staged()
    {
        var fixture = Build();
        var proposed = PolicyWithPin("246801");

        var staged = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with { ProtectedPayload = proposed }),
            Child);

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = staged.StagedDigest
            }), Administrator);

        Assert.True(response.Success, response.Message);
        Assert.Equal(proposed, fixture.Store.Read(ProtectedDocument.ParentPolicy));

        // And the proposal is gone, so a second prompt cannot reapply it.
        Assert.Null(fixture.Store.ReadStaged());
    }

    [Fact]
    public void A_staged_policy_swapped_while_the_prompt_is_open_is_refused()
    {
        // The attack the digest exists for: stage something innocuous, wait
        // for the parent to read the consent dialog, replace it in between.
        var fixture = Build();

        var staged = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with
            {
                ProtectedPayload = PolicyWithPin("246801")
            }), Child);

        fixture.Store.TamperStaged(PolicyWithPin("000000"));

        var response = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = staged.StagedDigest
            }), Administrator);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.NothingStaged, response.Reason);
        Assert.Null(fixture.Store.Read(ProtectedDocument.ParentPolicy));
    }

    [Fact]
    public void Committing_with_nothing_staged_is_refused()
    {
        var response = Build().Server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = new string('a', 64)
            }), Administrator);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.NothingStaged, response.Reason);
    }

    [Fact]
    public void A_policy_change_withdraws_every_capability()
    {
        // The rules changed. A parent who changes the PIN has ended the
        // sessions that were unlocked with the previous one.
        var fixture = Build(s => s.Seed(ProtectedDocument.ParentPolicy, PolicyWithPin("135790")));

        var capability = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.VerifyParentPin) with { ParentPinAttempt = "135790" }),
            Child).ParentCapability;

        Assert.True(fixture.Capabilities.IsValid(capability, Child));

        var staged = fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with
            {
                ProtectedPayload = PolicyWithPin("246801")
            }), Child);

        fixture.Server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = staged.StagedDigest
            }), Administrator);

        Assert.False(fixture.Capabilities.IsValid(capability, Child));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ZZZZ567890123456789012345678901234567890123456789012345678901234")]
    public void A_digest_that_is_not_a_digest_is_refused(string digest)
    {
        var response = Build().Server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = digest
            }), Administrator);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.PayloadInvalid, response.Reason);
    }

    // ------------------------------------------------- machine mutations

    [Fact]
    public void A_machine_mutation_is_refused_where_there_is_no_dispatcher()
    {
        // Nothing but the security service has one, and a build that grew a
        // second place to mutate Windows would be a build with two.
        var response = Build().Server.Handle(
            Line(Request(ElevatedOperationKind.CreateChildAccount) with { Username = "barn" }),
            Administrator);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.UnknownOperation, response.Reason);
    }

    [Fact]
    public void A_machine_mutation_from_a_child_never_reaches_the_dispatcher()
    {
        var reached = false;

        var server = new ElevatedBrokerServer(
            new InMemoryPrivilegedStore(),
            new ParentCapabilityRegistry(new FakeTimeProvider(Start)),
            new RecordingLogger(),
            new FakeTimeProvider(Start),
            new RecordingDispatcher(() => reached = true));

        var response = server.Handle(
            Line(Request(ElevatedOperationKind.CreateChildAccount) with { Username = "barn" }), Child);

        Assert.False(response.Success);
        Assert.False(reached, "an unauthorized request reached the machine dispatcher");
    }

    private sealed class RecordingDispatcher : IElevatedOperationDispatcher
    {
        private readonly Action _onDispatch;

        public RecordingDispatcher(Action onDispatch) => _onDispatch = onDispatch;

        public ElevatedResponse Dispatch(ElevatedRequest request)
        {
            _onDispatch();

            return new ElevatedResponse { RequestId = request.RequestId, Success = true };
        }
    }
}
