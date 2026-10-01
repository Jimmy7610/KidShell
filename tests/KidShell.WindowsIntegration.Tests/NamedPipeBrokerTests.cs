using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;
using KidShell.WindowsIntegration.Broker;
using Xunit;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>
/// The pipe itself: the access list, and one real round trip.
///
/// WHAT IS AND IS NOT TESTED HERE
/// ------------------------------
/// The broker's decisions are tested in KidShell.Core.Tests, against a
/// caller the test constructs. That is deliberate: the rules are where the
/// mistakes in this area live, and they do not need a pipe to be wrong.
///
/// What needs Windows, and is here, is the part that cannot be faked - the
/// descriptor the plan turns into, and the fact that a client and a server
/// built from these pieces actually talk. The round trip runs as the test
/// process's own identity, so what it proves is that the plumbing works and
/// that the caller is resolved from the token rather than from the message.
/// It does not prove that a child account is refused a policy write; nothing
/// on a single-account development machine could, and
/// docs/DEDICATED-DEVICE-VALIDATION.md is where that is recorded as
/// outstanding.
///
/// NOTHING HERE INSTALLS A SERVICE. The listener is an object in this
/// process, on a pipe name unique to the test.
/// </summary>
public class NamedPipeBrokerTests
{
    private const string ChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    // ------------------------------------------------------ the access list

    [Fact]
    public void Only_system_may_create_an_instance_of_the_pipe()
    {
        // The property that stops the endpoint being impersonated. Anyone
        // able to create an instance of KidShell.Security.v1 can answer a
        // client's questions as though they were the service, and for
        // VerifyParentPin that means answering yes.
        Assert.True(BrokerPipeAccessPlan.ForChild(ChildSid).OnlySystemMayCreateInstances);
        Assert.True(BrokerPipeAccessPlan.Unprovisioned().OnlySystemMayCreateInstances);
    }

    [Fact]
    public void The_descriptor_grants_the_child_no_more_than_read_and_write()
    {
        var security = NamedPipeBrokerListener.Describe(BrokerPipeAccessPlan.ForChild(ChildSid));

        var rules = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        var child = Assert.Single(rules, r => r.IdentityReference.Value == ChildSid);

        Assert.Equal(AccessControlType.Allow, child.AccessControlType);
        Assert.True(child.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(child.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(child.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
    }

    [Fact]
    public void A_provisioned_descriptor_names_nobody_else()
    {
        var security = NamedPipeBrokerListener.Describe(BrokerPipeAccessPlan.ForChild(ChildSid));

        var identities = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Select(r => r.IdentityReference.Value)
            .ToList();

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value;

        Assert.DoesNotContain(everyone, identities);
        Assert.DoesNotContain(authenticated, identities);

        // And an administrator can still get in to repair things.
        Assert.Contains(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value, identities);
    }

    [Fact]
    public void A_child_entry_with_no_sid_describes_nobody_rather_than_everybody()
    {
        // The failure mode worth guarding: an entry that describes nothing
        // must not become an entry that describes everyone.
        var plan = new BrokerPipeAccessPlan
        {
            IsChildScoped = true,
            Entries = [new BrokerPipeAce(BrokerPipePrincipal.ChildAccount, BrokerPipeRights.Connect)]
        };

        var security = NamedPipeBrokerListener.Describe(plan);

        Assert.Empty(security.GetAccessRules(true, false, typeof(SecurityIdentifier)));
    }

    [Fact]
    public void Only_system_and_administrators_may_rewrite_the_access_list()
    {
        // An owner can always rewrite a descriptor, and the owner is the
        // process that created the pipe - the LocalSystem service. The
        // descriptor does not try to SET the owner: Windows refuses an owner
        // a process is not entitled to name, so a creator that was not
        // already SYSTEM would fail to create the pipe at all and the
        // listener would retry forever. What the plan controls is who else
        // may change it, and the answer is administrators and nobody.
        var security = NamedPipeBrokerListener.Describe(BrokerPipeAccessPlan.ForChild(ChildSid));

        var mayChange = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(r => r.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions))
            .Select(r => r.IdentityReference.Value)
            .ToList();

        Assert.Equal(
            new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
            }.Order(),
            mayChange.Order());
    }

    [Fact]
    public void The_descriptor_can_actually_be_applied_to_a_pipe()
    {
        // The regression this exists for: the first descriptor named
        // LocalSystem as the owner, Windows refused it, and the listener
        // caught the exception and retried in a loop. The service would have
        // started, logged, and served nothing - which is the same class of
        // defect as a broker nothing could reach.
        var name = $"KidShell.Test.{Guid.NewGuid():n}";

        using var pipe = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096,
            NamedPipeBrokerListener.Describe(BrokerPipeAccessPlan.Unprovisioned()));

        Assert.NotNull(pipe);
    }

    [Fact]
    public void The_production_pipe_name_is_a_constant_the_client_does_not_choose()
    {
        // A client that could name its endpoint could be pointed at a pipe
        // somebody else created and have the parent's policy answered by
        // whoever created it.
        Assert.Equal("KidShell.Security.v1", BrokerEndpoint.PipeName);
        Assert.DoesNotContain('\\', BrokerEndpoint.PipeName);
    }

    // ------------------------------------------------------- a round trip

    [Fact]
    public async Task A_client_and_a_server_exchange_one_typed_message()
    {
        var name = $"KidShell.Test.{Guid.NewGuid():n}";
        var store = new InMemoryPrivilegedStore();
        var logger = new RecordingLogger();

        var server = new ElevatedBrokerServer(store, new ParentCapabilityRegistry(), logger);

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var listener = new NamedPipeBrokerListener(
            name, BrokerPipeAccessPlan.Unprovisioned(), server, logger);

        var serving = listener.RunAsync(stopping.Token);

        var client = new NamedPipeElevatedBrokerClient(name, logger);

        var response = await Task.Run(() => client.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.Probe,
            RequestId = "round-trip"
        }), stopping.Token);

        Assert.True(response.Success, response.Message);
        Assert.Equal("round-trip", response.RequestId);

        // A second request on a new connection. The service answers one
        // message per connection, so "it worked twice" is the property that
        // matters for a product that writes a counter on a timer.
        var second = await Task.Run(() => client.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.SaveScreenTimeState,
            RequestId = "second",
            ProtectedPayload =
                """{"schemaVersion":1,"localDate":"2026-09-30","usedSeconds":60,"sequence":1}""",
            DryRun = false
        }), stopping.Token);

        Assert.True(second.Success, second.Message);
        Assert.NotNull(store.Read(ProtectedDocument.ScreenTimeState));

        await stopping.CancelAsync();
        await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_decrement_sent_over_the_real_pipe_is_refused()
    {
        // The same rule as the unit test, proven once through the whole
        // transport - because a rule that is enforced in a class nobody
        // reaches is the shape of defect this pass exists for.
        var name = $"KidShell.Test.{Guid.NewGuid():n}";
        var store = new InMemoryPrivilegedStore();

        store.Seed(ProtectedDocument.ScreenTimeState,
            """{"schemaVersion":1,"localDate":"2026-09-30","usedSeconds":3600,"sequence":20}""");

        var logger = new RecordingLogger();
        var server = new ElevatedBrokerServer(store, new ParentCapabilityRegistry(), logger);

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var listener = new NamedPipeBrokerListener(
            name, BrokerPipeAccessPlan.Unprovisioned(), server, logger);

        var serving = listener.RunAsync(stopping.Token);

        var client = new NamedPipeElevatedBrokerClient(name, logger);

        var response = await Task.Run(() => client.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.SaveScreenTimeState,
            RequestId = "decrement",
            ProtectedPayload =
                """{"schemaVersion":1,"localDate":"2026-09-30","usedSeconds":0,"sequence":21}""",
            DryRun = false
        }), stopping.Token);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.TransitionRejected, response.Reason);
        Assert.Contains("3600", store.Read(ProtectedDocument.ScreenTimeState)!);

        await stopping.CancelAsync();
        await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_client_with_no_service_fails_closed_rather_than_throwing()
    {
        // Every development machine, and any machine where the service has
        // stopped. The product's answer to this is to refuse, so the client's
        // job is to report rather than to crash.
        var client = new NamedPipeElevatedBrokerClient(
            $"KidShell.Missing.{Guid.NewGuid():n}", new RecordingLogger());

        Assert.False(client.IsAvailable);

        var response = client.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.SaveScreenTimeState,
            RequestId = "no-service",
            ProtectedPayload = """{"schemaVersion":1,"localDate":"2026-09-30","usedSeconds":60}"""
        });

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.ServiceUnavailable, response.Reason);
    }

    [Fact]
    public async Task A_restarted_service_is_reached_by_the_same_client()
    {
        // One message per connection means a client holds nothing across a
        // restart, which is what makes this work without any reconnect logic
        // to get wrong.
        var name = $"KidShell.Test.{Guid.NewGuid():n}";
        var store = new InMemoryPrivilegedStore();
        var logger = new RecordingLogger();
        var client = new NamedPipeElevatedBrokerClient(name, logger);

        for (var run = 0; run < 2; run++)
        {
            var server = new ElevatedBrokerServer(store, new ParentCapabilityRegistry(), logger);

            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await using var listener = new NamedPipeBrokerListener(
                name, BrokerPipeAccessPlan.Unprovisioned(), server, logger);

            var serving = listener.RunAsync(stopping.Token);

            var response = await Task.Run(() => client.Send(new ElevatedRequest
            {
                Kind = ElevatedOperationKind.Probe,
                RequestId = $"run-{run}"
            }), stopping.Token);

            Assert.True(response.Success, $"run {run}: {response.Message}");

            await stopping.CancelAsync();
            await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    // ----------------------------------------------------------- framing

    [Fact]
    public async Task A_frame_larger_than_the_protocol_allows_is_refused_at_its_length()
    {
        // Nothing is allocated for it. A caller that announces four
        // gigabytes should be refused at the fourth byte, not at the fourth
        // gigabyte, and the receiver here runs as LocalSystem.
        using var stream = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF]);

        await Assert.ThrowsAsync<BrokerFrameException>(() =>
            BrokerFraming.ReadAsync(stream, BrokerEndpoint.MaxRequestBytes, CancellationToken.None));
    }

    [Fact]
    public async Task A_frame_that_ends_early_is_refused()
    {
        using var stream = new MemoryStream([0, 0, 0, 10, 1, 2, 3]);

        await Assert.ThrowsAsync<BrokerFrameException>(() =>
            BrokerFraming.ReadAsync(stream, BrokerEndpoint.MaxRequestBytes, CancellationToken.None));
    }

    [Fact]
    public async Task A_stream_that_ends_cleanly_is_an_ordinary_disconnection()
    {
        using var stream = new MemoryStream([]);

        Assert.Null(await BrokerFraming.ReadAsync(
            stream, BrokerEndpoint.MaxRequestBytes, CancellationToken.None));
    }

    [Fact]
    public async Task A_message_round_trips_through_the_framing()
    {
        using var stream = new MemoryStream();

        await BrokerFraming.WriteAsync(stream, "hej hej", 1024, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal("hej hej", await BrokerFraming.ReadAsync(stream, 1024, CancellationToken.None));
    }
}

