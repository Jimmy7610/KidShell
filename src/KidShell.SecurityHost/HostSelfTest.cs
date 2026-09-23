using KidShell.Core.Diagnostics;
using KidShell.WindowsIntegration.Broker;

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
