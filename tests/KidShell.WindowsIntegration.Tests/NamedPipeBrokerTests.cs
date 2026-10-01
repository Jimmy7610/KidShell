using System.Diagnostics;
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
    //
    // THE CI FAILURE THESE ARE WRITTEN AROUND
    //
    // Three of these tests failed on GitHub Actions and passed on every
    // developer machine. The listener wrote its reply and then called
    // Disconnect(), which forces the client off and DISCARDS anything the
    // client has not read yet - so on a two-core runner with a contended
    // thread pool the answer was thrown away and the client saw a clean
    // end of stream. It reported "the service did not respond", which is
    // indistinguishable from the service not being there: a refusal the
    // broker had correctly decided came back as ServiceUnavailable.
    //
    // It was NOT a startup race. The listener creates its pipe synchronously
    // before RunAsync reaches its first await, and the failing client had
    // plainly connected - a pipe that did not exist yet would have produced a
    // connect timeout, not a reply that went missing. Readiness is now
    // explicit anyway, because an implicit guarantee that depends on where
    // the first await happens is one refactoring away from being untrue.

    /// <summary>
    /// A listener and a client on a pipe name unique to the test, with no
    /// sleeps anywhere: start-up waits on <see cref="NamedPipeBrokerListener.Ready"/>.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly NamedPipeBrokerListener _listener;
        private readonly CancellationTokenSource _stopping;
        private readonly Task _serving;

        private Harness(
            string pipeName,
            InMemoryPrivilegedStore store,
            RecordingLogger logger,
            NamedPipeBrokerListener listener,
            CancellationTokenSource stopping)
        {
            PipeName = pipeName;
            Store = store;
            Logger = logger;
            _listener = listener;
            _stopping = stopping;
            _serving = listener.RunAsync(stopping.Token);
        }

        public string PipeName { get; }

        public InMemoryPrivilegedStore Store { get; }

        public RecordingLogger Logger { get; }

        public NamedPipeElevatedBrokerClient Client => new(PipeName, Logger);

        public static async Task<Harness> StartAsync(
            InMemoryPrivilegedStore? store = null, string? pipeName = null)
        {
            var name = pipeName ?? $"KidShell.Test.{Guid.NewGuid():n}";
            var vault = store ?? new InMemoryPrivilegedStore();
            var logger = new RecordingLogger();

            var harness = new Harness(
                name, vault, logger,
                new NamedPipeBrokerListener(
                    name, BrokerPipeAccessPlan.Unprovisioned(),
                    new ElevatedBrokerServer(vault, new ParentCapabilityRegistry(), logger),
                    logger),
                new CancellationTokenSource(TimeSpan.FromSeconds(30)));

            // The whole point of the readiness signal. No delay, no retry
            // loop, no "it is probably up by now".
            await harness._listener.Ready.ConfigureAwait(false);

            return harness;
        }

        /// <summary>
        /// Sends one request off the test's own thread.
        ///
        /// Send blocks, and blocking the thread xUnit handed this test would
        /// make the suite's own concurrency limit part of what is being
        /// measured.
        /// </summary>
        public Task<ElevatedResponse> SendAsync(ElevatedRequest request) =>
            Task.Run(() => Client.Send(request));

        /// <summary>
        /// A response plus what the broker said about it.
        ///
        /// The broker deliberately tells the caller a closed reason and a
        /// sentence, and keeps the detail in its own log. That is right in
        /// production and useless in a failing test, so the assertion message
        /// carries the log.
        /// </summary>
        public string Explain(ElevatedResponse response) =>
            $"{response.Reason}: {response.Message}{Environment.NewLine}" +
            string.Join(Environment.NewLine,
                Logger.Entries.Select(e => $"  {e.Level} {e.Category}: {e.Message} {e.Exception}"));

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            await Task.WhenAny(_serving, Task.Delay(TimeSpan.FromSeconds(5)));
            _stopping.Dispose();
            await _listener.DisposeAsync();
        }
    }

    private static string Counter(int used, int sequence) =>
        $"{{\"schemaVersion\":1,\"localDate\":\"2026-09-30\",\"usedSeconds\":{used},\"sequence\":{sequence}}}";

    // --------------------------------------------------------- readiness

    [Fact]
    public async Task Readiness_completes_once_the_pipe_exists()
    {
        await using var harness = await Harness.StartAsync();

        // StartAsync already awaited it; this says what was awaited.
        Assert.True(harness.Client.IsAvailable);
    }

    [Fact]
    public async Task A_listener_that_cannot_create_its_pipe_never_reports_ready()
    {
        // The failure mode worth refusing to paper over. The listener catches
        // a creation failure and retries, so a service whose endpoint cannot
        // be created looks alive - and a readiness signal that completed
        // anyway would make a test green against a broker that serves
        // nothing.
        var logger = new RecordingLogger();
        var name = $"KidShell.Taken.{Guid.NewGuid():n}";

        // The name is occupied by a pipe that allows exactly one instance, so
        // the listener's own Create fails every time it tries - which is what
        // "the endpoint cannot be stood up" looks like in practice.
        using var squatter = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        await using var listener = new NamedPipeBrokerListener(
            name,
            BrokerPipeAccessPlan.Unprovisioned(),
            new ElevatedBrokerServer(
                new InMemoryPrivilegedStore(), new ParentCapabilityRegistry(), logger),
            logger);

        using var stopping = new CancellationTokenSource();
        var serving = listener.RunAsync(stopping.Token);

        var finished = await Task.WhenAny(listener.Ready, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.NotSame(listener.Ready, finished);
        Assert.False(listener.Ready.IsCompletedSuccessfully);

        await stopping.CancelAsync();
        await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(5)));

        // And once it has stopped, awaiting readiness answers rather than
        // hanging until the caller's own timeout.
        Assert.True(listener.Ready.IsCompleted);
        Assert.False(listener.Ready.IsCompletedSuccessfully);
    }

    // -------------------------------------------------------- round trips

    [Fact]
    public async Task A_client_and_a_server_exchange_one_typed_message()
    {
        await using var harness = await Harness.StartAsync();

        var response = await harness.SendAsync(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.Probe,
            RequestId = "round-trip"
        });

        Assert.True(response.Success, harness.Explain(response));
        Assert.Equal("round-trip", response.RequestId);

        // A second request on a new connection. The service answers one
        // message per connection, so "it worked twice" is the property that
        // matters for a product that writes a counter on a timer.
        var second = await harness.SendAsync(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.SaveScreenTimeState,
            RequestId = "second",
            ProtectedPayload = Counter(60, 1),
            DryRun = false
        });

        Assert.True(second.Success, second.Message);
        Assert.NotNull(harness.Store.Read(ProtectedDocument.ScreenTimeState));
    }

    [Fact]
    public async Task A_reply_survives_a_client_that_is_slow_to_read()
    {
        // THE REGRESSION TEST FOR THE CI FAILURE, and the reason it is
        // written against the frames rather than through the client: the
        // defect was that the server discarded a reply the client had not
        // read yet, so the test has to be the slow reader itself.
        //
        // Measured before the fix: with Disconnect() and a 50ms gap the reply
        // was lost every time. It is a real delay rather than a sleep
        // standing in for a race - the delay IS the condition under test.
        await using var harness = await Harness.StartAsync();

        using var pipe = new NamedPipeClientStream(
            ".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

        await pipe.ConnectAsync(5000);

        await BrokerFraming.WriteAsync(
            pipe,
            ElevatedProtocol.Serialize(new ElevatedRequest
            {
                Kind = ElevatedOperationKind.Probe,
                RequestId = "slow-reader"
            }),
            BrokerEndpoint.MaxRequestBytes,
            CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(250));

        var line = await BrokerFraming.ReadAsync(
            pipe, BrokerEndpoint.MaxResponseBytes, CancellationToken.None);

        Assert.NotNull(line);

        var response = ElevatedProtocol.DeserializeResponse(line);

        Assert.NotNull(response);
        Assert.True(response.Success, response.Message);
        Assert.Equal("slow-reader", response.RequestId);
    }

    [Fact]
    public async Task A_decrement_sent_over_the_real_pipe_is_refused()
    {
        // The same rule as the unit test, proven once through the whole
        // transport - because a rule enforced in a class nobody reaches is
        // the shape of defect this whole pass exists for.
        var store = new InMemoryPrivilegedStore();
        store.Seed(ProtectedDocument.ScreenTimeState, Counter(3600, 20));

        await using var harness = await Harness.StartAsync(store);

        var response = await harness.SendAsync(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.SaveScreenTimeState,
            RequestId = "decrement",
            ProtectedPayload = Counter(0, 21),
            DryRun = false
        });

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.TransitionRejected, response.Reason);
        Assert.Contains("3600", store.Read(ProtectedDocument.ScreenTimeState)!);
    }

    [Fact]
    public async Task A_slow_caller_does_not_stop_the_broker_for_everybody()
    {
        // The other defect the CI failure uncovered. The serve loop caught
        // every OperationCanceledException, including its own per-exchange
        // deadline - so a caller that connected and said nothing stopped the
        // broker permanently, and the parent's policy could not be written
        // again until the service restarted. One connect.
        await using var harness = await Harness.StartAsync();

        using (var silent = new NamedPipeClientStream(
                   ".", harness.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                   TokenImpersonationLevel.Identification))
        {
            await silent.ConnectAsync(5000);

            // Connected, and says nothing. Abandoning the connection is what
            // a crashed caller does, and the server's deadline covers the
            // case where it does not even do that.
        }

        var response = await harness.SendAsync(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.Probe,
            RequestId = "after-the-silent-one"
        });

        Assert.True(response.Success, harness.Explain(response));
    }

    [Fact]
    public void A_client_with_no_service_fails_closed_within_a_bounded_time()
    {
        // Every development machine, and any machine where the service has
        // stopped. The product's answer is to refuse, so the client's job is
        // to report rather than to crash - and to do it quickly, because the
        // screen-time counter is written on a timer and a shell that stalls
        // for a minute per tick has replaced one failure with another.
        var client = new NamedPipeElevatedBrokerClient(
            $"KidShell.Missing.{Guid.NewGuid():n}", new RecordingLogger());

        var started = Stopwatch.StartNew();

        var response = client.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.SaveScreenTimeState,
            RequestId = "no-service",
            ProtectedPayload = Counter(60, 1)
        });

        started.Stop();

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.ServiceUnavailable, response.Reason);

        // The connect timeout plus generous room for a loaded runner.
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(20),
            $"the client took {started.Elapsed.TotalSeconds:F1}s to fail closed");
    }

    [Fact]
    public async Task A_restarted_service_is_reached_by_the_same_client()
    {
        // One message per connection means a client holds nothing across a
        // restart, which is what makes this work without any reconnect logic
        // to get wrong. The store is shared, as it would be on a real
        // machine: the service restarts, the protected documents do not.
        var store = new InMemoryPrivilegedStore();
        var name = $"KidShell.Test.{Guid.NewGuid():n}";
        var client = new NamedPipeElevatedBrokerClient(name, new RecordingLogger());

        for (var run = 0; run < 2; run++)
        {
            await using var harness = await Harness.StartAsync(store, name);

            var response = await Task.Run(() => client.Send(new ElevatedRequest
            {
                Kind = ElevatedOperationKind.Probe,
                RequestId = $"run-{run}"
            }));

            Assert.True(response.Success, $"run {run}: {response.Message}");
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

