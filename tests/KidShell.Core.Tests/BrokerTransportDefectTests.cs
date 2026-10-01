using System.Diagnostics;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// THE DEFECT THIS PASS EXISTS FOR.
///
/// The protected-write broker introduced in OPSV retest 2 is the right shape -
/// a child that reads and never writes, a privileged side that owns every
/// destination - and its production transport could not work.
///
/// <c>ProcessElevatedBrokerClient</c> starts KidShell.SecurityHost with
/// UseShellExecute off and the standard streams redirected.
/// KidShell.SecurityHost's manifest asks for asInvoker, and Program.cs refuses
/// to enter the request loop unless the process is genuinely elevated. So on a
/// real child account the helper starts unelevated, prints a line to stderr
/// and exits, and every protected write fails.
///
/// The source comments said the helper is launched with the "runas" verb. The
/// production client never did, and could not: a verb needs ShellExecute, and
/// redirection forbids it.
///
/// These tests fail against the old design. They are kept afterwards because
/// the reason the design was wrong is not obvious from the code that replaced
/// it.
/// </summary>
public class BrokerTransportDefectTests
{
    private static ProcessElevatedBrokerClient Production() =>
        new(@"C:\Program Files\KidShell\KidShell.SecurityHost.exe", new RecordingLogger());

    // ------------------------------------------------- the contradiction

    [Fact]
    public void The_process_transport_cannot_reach_an_elevated_helper()
    {
        var viability = Production().LaunchPlan.Evaluate();

        Assert.False(viability.CanSucceed, viability.Reason);
        Assert.Contains("elevation", viability.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_process_transport_does_not_ask_windows_to_elevate_anything()
    {
        var plan = Production().LaunchPlan;

        // What the comments claimed, against what the code did.
        Assert.Equal(string.Empty, plan.Verb);
        Assert.False(plan.UseShellExecute);
        Assert.Equal(BrokerManifestElevation.AsInvoker, plan.Manifest);
        Assert.Equal(BrokerElevationRequirement.Administrator, plan.Requires);
    }

    // --------------------------------------- why runas was not the answer

    [Fact]
    public void Adding_the_runas_verb_to_the_same_plan_is_not_a_fix()
    {
        // The obvious repair, and it does not compile into a working launch:
        // a verb is ignored without ShellExecute, and ShellExecute cannot
        // redirect the streams the protocol reads and writes.
        var withVerb = Production().LaunchPlan with { Verb = "runas" };

        Assert.False(withVerb.Evaluate().CanSucceed);
        Assert.Contains("ShellExecute", withVerb.Evaluate().Reason, StringComparison.Ordinal);

        var withShellExecute = withVerb with { UseShellExecute = true };

        Assert.False(withShellExecute.Evaluate().CanSucceed);
        Assert.Contains("redirect", withShellExecute.Evaluate().Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dotnet_itself_refuses_shell_execute_with_redirected_streams()
    {
        // Not a restatement of the rule - the runtime enforcing it. Nothing
        // is launched: the check happens before Process.Start resolves the
        // file name, which is why a path that does not exist is safe here.
        var start = new ProcessStartInfo
        {
            FileName = @"C:\KidShell-does-not-exist\nothing.exe",
            Verb = "runas",
            UseShellExecute = true,
            RedirectStandardInput = true
        };

        Assert.Throws<InvalidOperationException>(() => Process.Start(start));
    }

    [Fact]
    public void A_viable_runas_plan_would_prompt_on_every_request()
    {
        // Suppose the protocol were rewritten so no redirection was needed.
        // The transport would then work and still be wrong for this product:
        // the screen-time counter and the PIN throttle are written on a timer
        // during ordinary use, and a shell a child operates cannot raise a
        // consent prompt every thirty seconds.
        var plan = Production().LaunchPlan with
        {
            Verb = "runas",
            UseShellExecute = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false
        };

        Assert.True(plan.Evaluate().CanSucceed);
        Assert.True(plan.PromptsPerRequest);
    }

    // ---------------------------------------------- what replaced it

    // -------------------------------------------- the cost of asking twice

    [Fact]
    public void One_protected_write_opens_the_pipe_once()
    {
        // The first version asked IsAvailable and then sent, which is two
        // connections per write. On a machine with no service that is two
        // connect timeouts every thirty seconds, because the screen-time
        // counter is written on a timer - seconds of stall, repeatedly, on
        // exactly the machines where the product is already in trouble.
        var broker = new CountingBroker(new ElevatedResponse
        {
            RequestId = "x", Success = true
        });

        var writer = new BrokeredProtectedStateWriter(broker, new RecordingLogger());

        Assert.True(writer.SaveScreenTimeState("""{"schemaVersion":1}""").Success);

        Assert.Equal(1, broker.Sends);
        Assert.Equal(0, broker.AvailabilityChecks);
    }

    [Fact]
    public void An_unreachable_service_is_reported_as_unavailable_not_refused()
    {
        // The product does different things with the two. "There is nowhere
        // to write this" makes enforcement unavailable; "what you asked for
        // is not allowed" is a bug or an attack.
        var broker = new CountingBroker(new ElevatedResponse
        {
            RequestId = "x",
            Success = false,
            Reason = BrokerFailureReason.ServiceUnavailable,
            Message = "no service"
        });

        var result = new BrokeredProtectedStateWriter(broker, new RecordingLogger())
            .SaveScreenTimeState("""{"schemaVersion":1}""");

        Assert.Equal(ProtectedWriteStatus.WriterUnavailable, result.Status);
    }

    private sealed class CountingBroker : IElevatedBrokerClient
    {
        private readonly ElevatedResponse _response;

        public CountingBroker(ElevatedResponse response) => _response = response;

        public int Sends { get; private set; }

        public int AvailabilityChecks { get; private set; }

        public bool IsAvailable
        {
            get
            {
                AvailabilityChecks++;
                return true;
            }
        }

        public ElevatedResponse Send(ElevatedRequest request)
        {
            Sends++;
            return _response with { RequestId = request.RequestId };
        }
    }

    [Fact]
    public void The_named_pipe_transport_needs_no_elevation_of_its_own()
    {
        // The client connects to a service that is already running as
        // LocalSystem. Nothing is started, so there is nothing to elevate and
        // nothing to prompt about.
        var plan = new NamedPipeElevatedBrokerClient(
            BrokerEndpoint.PipeName, new RecordingLogger()).LaunchPlan;

        Assert.True(plan.Evaluate().CanSucceed);
        Assert.False(plan.PromptsPerRequest);
        Assert.Equal(string.Empty, plan.FileName);
    }
}
