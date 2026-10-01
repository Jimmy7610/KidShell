using System.Diagnostics;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// Sends one typed request to the elevated helper and reads its answer.
///
/// An interface because the helper is a separate elevated process, and a test
/// must be able to exercise the whole request path without one - the point of
/// the boundary is that the unprivileged side cannot do the work itself, which
/// also means a test cannot simply let it.
/// </summary>
public interface IElevatedBrokerClient
{
    /// <summary>Whether the helper could be reached at all.</summary>
    bool IsAvailable { get; }

    ElevatedResponse Send(ElevatedRequest request);
}

/// <summary>
/// The production client: runs KidShell.SecurityHost and speaks the
/// line-per-request protocol over its standard streams.
///
/// WHY IT IS NOT AVAILABLE ON THIS MACHINE
/// ---------------------------------------
/// The helper requires elevation and exits with a message if it does not have
/// it, so on an ordinary developer machine <see cref="IsAvailable"/> is false
/// and every write reports WriterUnavailable. That is the honest answer, and a
/// production build fails closed on it rather than writing the parent's policy
/// somewhere the child can reach.
///
/// Nothing here installs a service, registers a task, or elevates anything by
/// itself. It starts a process that the operating system will refuse to
/// elevate without a prompt, which is what a privileged broker should be.
/// </summary>
public sealed class ProcessElevatedBrokerClient : IElevatedBrokerClient
{
    private readonly string _hostPath;
    private readonly IKidShellLogger _logger;

    public ProcessElevatedBrokerClient(string hostPath, IKidShellLogger logger)
    {
        _hostPath = hostPath;
        _logger = logger;
    }

    public bool IsAvailable => File.Exists(_hostPath);

    /// <summary>
    /// Exactly what this client does to reach the helper.
    ///
    /// Not a description of it - the value <see cref="Send"/> builds its
    /// <see cref="ProcessStartInfo"/> from, so a test that reads this is
    /// reading the production path rather than a second copy of it that can
    /// drift. See <see cref="BrokerLaunchPlan"/> for why that matters.
    /// </summary>
    public BrokerLaunchPlan LaunchPlan => new()
    {
        FileName = _hostPath,
        Verb = string.Empty,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,

        // The helper refuses to enter its request loop unless the token is
        // elevated, and its manifest asks for nothing.
        Requires = BrokerElevationRequirement.Administrator,
        Manifest = BrokerManifestElevation.AsInvoker
    };

    public ElevatedResponse Send(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAvailable)
        {
            return ElevatedResponse.Reject(request.RequestId, "Rättighetshjälparen finns inte.");
        }

        try
        {
            var plan = LaunchPlan;

            var start = new ProcessStartInfo
            {
                FileName = plan.FileName,
                RedirectStandardInput = plan.RedirectStandardInput,
                RedirectStandardOutput = plan.RedirectStandardOutput,
                RedirectStandardError = true,
                UseShellExecute = plan.UseShellExecute,
                CreateNoWindow = true
            };

            if (plan.Verb.Length > 0)
            {
                start.Verb = plan.Verb;
            }

            using var process = Process.Start(start);

            if (process is null)
            {
                return ElevatedResponse.Reject(request.RequestId, "Rättighetshjälparen kunde inte startas.");
            }

            process.StandardInput.WriteLine(ElevatedProtocol.Serialize(request));
            process.StandardInput.Close();

            var line = process.StandardOutput.ReadLine();
            process.WaitForExit(BrokerTimeoutMilliseconds);

            var response = line is null ? null : ElevatedProtocol.DeserializeResponse(line);

            return response ?? ElevatedResponse.Reject(request.RequestId, "Inget svar från rättighetshjälparen.");
        }
        catch (Exception ex)
        {
            // Expected without elevation. Not an error worth throwing over:
            // the caller's job is to fail closed, not to crash.
            _logger.Warning("Storage", "The elevated helper could not be reached.", ex);
            return ElevatedResponse.Reject(request.RequestId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Long enough for a file write behind a UAC prompt, short enough that a
    /// wedged helper does not hang the shell.
    /// </summary>
    private const int BrokerTimeoutMilliseconds = 30_000;
}

/// <summary>
/// Turns a typed save into a typed request.
///
/// This is the whole of the unprivileged side of OPSV retest 2 finding 01. It
/// knows WHICH document it is saving and nothing at all about where documents
/// live - there is no path here to get wrong, and none to pass on.
/// </summary>
public sealed class BrokeredProtectedStateWriter : IProtectedStateWriter
{
    private readonly IElevatedBrokerClient _broker;
    private readonly IKidShellLogger _logger;

    public BrokeredProtectedStateWriter(IElevatedBrokerClient broker, IKidShellLogger logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public bool IsAvailable => _broker.IsAvailable;

    public ProtectedWriteResult SaveParentPolicy(string json) =>
        Send(ElevatedOperationKind.SaveParentPolicy, json);

    /// <summary>
    /// Offers a policy for approval.
    ///
    /// The digest comes back from the SERVICE, computed over what the service
    /// actually stored. Returning a digest this side computed would prove
    /// only that this side can hash, and the whole purpose of carrying one is
    /// that the approver and the stager might disagree.
    /// </summary>
    public ProtectedStageResult StageParentPolicy(string json)
    {
        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedStageResult.Reject(problem);
        }

        // Staging happens when a parent saves settings: once, deliberately,
        // not on a timer. The pre-check is kept here because a clear "the
        // service is not installed" is worth more on that path than one
        // avoided connection.
        if (!_broker.IsAvailable)
        {
            return ProtectedStageResult.Unavailable("Rättighetstjänsten är inte tillgänglig.");
        }

        var response = _broker.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.StageParentPolicy,
            RequestId = Guid.NewGuid().ToString("n"),
            ProtectedPayload = json,
            DryRun = false
        });

        if (!response.Success || string.IsNullOrWhiteSpace(response.StagedDigest))
        {
            _logger.Warning(BrokerAudit.Category,
                $"The parent policy could not be staged: {response.Message}");

            return response.Rejected
                ? ProtectedStageResult.Reject(response.Message)
                : ProtectedStageResult.Fail(response.Message);
        }

        return ProtectedStageResult.Staged(response.StagedDigest);
    }

    public ProtectedWriteResult SaveScreenTimeState(string json) =>
        Send(ElevatedOperationKind.SaveScreenTimeState, json);

    public ProtectedWriteResult SavePinThrottleState(string json) =>
        Send(ElevatedOperationKind.SavePinThrottleState, json);

    public ProtectedWriteResult MarkProvisioned(string json) =>
        Send(ElevatedOperationKind.MarkProvisioned, json);

    private ProtectedWriteResult Send(ElevatedOperationKind kind, string json)
    {
        // Checked here so a bug is caught before it crosses the boundary. The
        // helper checks again, because that is the check that matters.
        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedWriteResult.Reject(problem);
        }

        // NO AVAILABILITY PRE-CHECK. The send is the check.
        //
        // Asking first meant opening the pipe twice for every write, and on
        // a machine with no service that is two connect timeouts per
        // screen-time tick - seconds of stall, on a timer, on exactly the
        // machines where the product is already in trouble.
        var response = _broker.Send(new ElevatedRequest
        {
            Kind = kind,
            RequestId = Guid.NewGuid().ToString("n"),
            ProtectedPayload = json,
            DryRun = false
        });

        if (response.Success)
        {
            return ProtectedWriteResult.Ok();
        }

        _logger.Warning("Storage", $"Protected write {kind} was not completed: {response.Message}");

        if (response.Reason == BrokerFailureReason.ServiceUnavailable)
        {
            // Kept distinct from a refusal. "There is nowhere to write this"
            // and "what you asked for is not allowed" lead to different
            // product behaviour: the first makes enforcement unavailable,
            // the second is a bug or an attack.
            return ProtectedWriteResult.Unavailable(response.Message);
        }

        return response.Rejected
            ? ProtectedWriteResult.Reject(response.Message)
            : ProtectedWriteResult.Fail(response.Message);
    }
}
