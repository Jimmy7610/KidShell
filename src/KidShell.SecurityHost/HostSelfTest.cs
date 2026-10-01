using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;

namespace KidShell.SecurityHost;

/// <summary>
/// Proves the helper starts and rejects bad input, without touching Windows.
///
/// This is what CI runs. It deliberately exercises only the parse-and-reject
/// half of the dispatcher: a self-test that performed a real operation would be
/// a self-test that changed the build agent.
/// </summary>
public sealed class HostSelfTest
{
    private readonly IKidShellLogger _logger;

    public HostSelfTest(IKidShellLogger logger) => _logger = logger;

    public async Task<bool> RunAsync()
    {
        var failures = new List<string>();

        // Malformed input must be rejected rather than guessed at.
        Check(failures, "malformed JSON is rejected",
            ElevatedProtocol.DeserializeRequest("{not json") is null);

        // Validation must reject each hostile shape.
        Check(failures, "empty user name is rejected",
            ElevatedRequestValidator.ValidateUserName("") is not null);

        Check(failures, "over-long user name is rejected",
            ElevatedRequestValidator.ValidateUserName(new string('a', 21)) is not null);

        Check(failures, "user name with a backslash is rejected",
            ElevatedRequestValidator.ValidateUserName("a\\b") is not null);

        Check(failures, "path traversal in a SID is rejected",
            ElevatedRequestValidator.ValidateSid(@"S-1-5-21-..\..\SOFTWARE") is not null);

        Check(failures, "non-SID text is rejected",
            ElevatedRequestValidator.ValidateSid("administrator") is not null);

        Check(failures, "a real SID shape is accepted",
            ElevatedRequestValidator.ValidateSid("S-1-5-21-1111111111-2222222222-3333333333-1001") is null);

        Check(failures, "AUMID with a quote is rejected",
            ElevatedRequestValidator.ValidateAumid("Pkg_abc!App\"x") is not null);

        Check(failures, "relative executable path is rejected",
            ElevatedRequestValidator.ValidateExecutablePath(@"..\evil.exe") is not null);

        Check(failures, "non-exe path is rejected",
            ElevatedRequestValidator.ValidateExecutablePath(@"C:\Windows\System32\cmd.dll") is not null);

        // And the protocol must round-trip a legitimate request.
        var request = new ElevatedRequest
        {
            Kind = ElevatedOperationKind.Probe,
            RequestId = "self-test"
        };

        var roundTripped = ElevatedProtocol.DeserializeRequest(ElevatedProtocol.Serialize(request));

        Check(failures, "a probe request round-trips",
            roundTripped is not null && roundTripped.Kind == ElevatedOperationKind.Probe);

        // --------------------------------------------- the authority split
        //
        // PRIVILEGED BROKER HARDENING. The checks above prove the binary
        // parses and rejects; these prove it still believes the thing it was
        // rebuilt for. They are pure decisions over values, so running them
        // here installs nothing, starts nothing, opens no pipe and touches no
        // file - which is the whole constraint on a self-test.
        var child = new BrokerCaller
        {
            Class = BrokerCallerClass.ChildSession,
            Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
            AccountName = "self-test"
        };

        Check(failures, "a child session cannot write the parent policy",
            !BrokerAuthorizationPolicy.Decide(
                child, ElevatedOperationKind.SaveParentPolicy, false).Allowed);

        Check(failures, "a child session cannot mark the machine provisioned",
            !BrokerAuthorizationPolicy.Decide(
                child, ElevatedOperationKind.MarkProvisioned, false).Allowed);

        Check(failures, "a child session cannot reset today's counter",
            !BrokerAuthorizationPolicy.Decide(
                child, ElevatedOperationKind.ResetScreenTimeToday, false).Allowed);

        Check(failures, "an unidentified caller cannot even probe",
            !BrokerAuthorizationPolicy.Decide(
                BrokerCaller.Unknown, ElevatedOperationKind.Probe, false).Allowed);

        Check(failures, "only SYSTEM may create an instance of the pipe",
            BrokerPipeAccessPlan.ForChild(child.Sid).OnlySystemMayCreateInstances &&
            BrokerPipeAccessPlan.Unprovisioned().OnlySystemMayCreateInstances);

        // The service is described and not installed. Running --self-test on
        // a build agent must leave no service behind, and the way to be sure
        // of that is for the self-test to have no code that installs one.
        Check(failures, "the service plan names LocalSystem and automatic start",
            SecurityHostService.Account == "LocalSystem" && SecurityHostService.StartType == "auto");

        Check(failures, "an image path outside Program Files is refused",
            !SecurityHostService.IsAcceptableImagePath(
                @"C:\Users\child\KidShell.SecurityHost.exe", @"C:\Program Files"));

        await Task.CompletedTask.ConfigureAwait(false);

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"FAIL: {failure}");
            }

            return false;
        }

        Console.WriteLine("KidShell.SecurityHost self-test passed. Nothing was changed.");
        _logger.Info("SelfTest", "Self-test passed.");
        return true;
    }

    private static void Check(List<string> failures, string what, bool condition)
    {
        if (!condition)
        {
            failures.Add(what);
        }
    }
}
