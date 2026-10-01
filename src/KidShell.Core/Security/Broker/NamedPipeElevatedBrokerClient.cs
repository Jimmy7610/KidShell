using System.IO.Pipes;
using System.Security.Principal;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// The production transport: a named pipe to a service that is already
/// privileged.
///
/// WHAT CHANGED, AND WHY IT HAD TO
/// -------------------------------
/// The previous transport started KidShell.SecurityHost per request and
/// expected it to be elevated. It never was - see
/// <see cref="BrokerLaunchPlan"/> and the tests that reproduce it - and the
/// repair is not to add a UAC prompt. Screen time is written on a timer and
/// the PIN throttle on every attempt; a shell a child operates cannot raise a
/// consent prompt during ordinary enforcement, and a parent taught to click
/// through prompts has been taught the wrong lesson.
///
/// So the privilege is held by a service that runs as LocalSystem, and this
/// client connects to it. Nothing is started here, so there is nothing to
/// elevate: the connection either reaches a running service or it does not,
/// and "it does not" means enforcement is unavailable, which this product
/// already knows how to fail closed on.
///
/// WHAT THIS CLIENT IS NOT ALLOWED TO DECIDE
/// -----------------------------------------
/// The pipe name. It is a compile-time constant, taken from
/// <see cref="BrokerEndpoint"/>. A client that could be pointed at another
/// endpoint could have the parent's policy answered by whoever created it.
/// The constructor takes one only so a test can run two servers at once, and
/// production resolves the constant.
/// </summary>
public sealed class NamedPipeElevatedBrokerClient : IElevatedBrokerClient
{
    private readonly string _pipeName;
    private readonly IKidShellLogger _logger;

    public NamedPipeElevatedBrokerClient(string pipeName, IKidShellLogger logger)
    {
        _pipeName = pipeName;
        _logger = logger;
    }

    /// <summary>
    /// Nothing is launched, so there is nothing to elevate.
    ///
    /// Stated as a value for the same reason the old one was: so a test can
    /// compare the two rather than take a comment's word for it.
    /// </summary>
    public BrokerLaunchPlan LaunchPlan => new()
    {
        FileName = string.Empty,
        Verb = string.Empty,
        UseShellExecute = false,
        RedirectStandardInput = false,
        RedirectStandardOutput = false,
        Requires = BrokerElevationRequirement.None,
        Manifest = BrokerManifestElevation.AsInvoker
    };

    private readonly Lock _gate = new();
    private bool _reachable;
    private DateTimeOffset _lastAnswerUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Whether the service is listening.
    ///
    /// A real connection attempt, because the question "can this machine
    /// persist security state" has no cheaper honest answer. A
    /// file-existence check was the old one, and it was true on every
    /// machine where the write then failed.
    ///
    /// REMEMBERED FOR A FEW SECONDS, AND THAT IS NOT AN OPTIMISATION
    /// -------------------------------------------------------------
    /// The first version connected on every call, and the callers ask before
    /// every write. On a machine with no service that is a two-second
    /// timeout per probe, and the screen-time counter is written on a
    /// thirty-second timer - so the shell would have stalled for seconds at
    /// a time, repeatedly, on exactly the machines where the product is
    /// already in trouble.
    ///
    /// <see cref="Send"/> updates the same answer from its own outcome, so
    /// in steady use no extra connection is made at all: the writes
    /// themselves are the probe.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                if (DateTimeOffset.UtcNow - _lastAnswerUtc < AvailabilityMemory)
                {
                    return _reachable;
                }
            }

            try
            {
                using var pipe = Connect();
                Remember(true);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException
                                          or UnauthorizedAccessException or InvalidOperationException)
            {
                Remember(false);
                return false;
            }
        }
    }

    /// <summary>
    /// Short. Long enough that one write does not probe twice, short enough
    /// that a service that has just started is noticed within a tick.
    /// </summary>
    private static readonly TimeSpan AvailabilityMemory = TimeSpan.FromSeconds(5);

    private void Remember(bool reachable)
    {
        lock (_gate)
        {
            _reachable = reachable;
            _lastAnswerUtc = DateTimeOffset.UtcNow;
        }
    }

    public ElevatedResponse Send(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var pipe = Connect();
            using var cancellation = new CancellationTokenSource(BrokerEndpoint.IoTimeoutMilliseconds);

            BrokerFraming
                .WriteAsync(pipe, ElevatedProtocol.Serialize(request),
                    BrokerEndpoint.MaxRequestBytes, cancellation.Token)
                .GetAwaiter().GetResult();

            var line = BrokerFraming
                .ReadAsync(pipe, BrokerEndpoint.MaxResponseBytes, cancellation.Token)
                .GetAwaiter().GetResult();

            if (line is null)
            {
                return Unavailable(request, "Rättighetstjänsten svarade inte.");
            }

            var response = ElevatedProtocol.DeserializeResponse(line);

            if (response is null)
            {
                return Unavailable(request, "Svaret från rättighetstjänsten kunde inte tolkas.");
            }

            // The send is itself the most reliable availability answer there
            // is, so it is the one remembered.
            Remember(true);

            if (response.ProtocolVersion != BrokerEndpoint.ProtocolVersion)
            {
                // A service from a different build. Refusing is the only safe
                // reading: a response whose shape is unknown cannot be
                // interpreted as success.
                _logger.Error(BrokerAudit.Category,
                    $"The security service speaks protocol {response.ProtocolVersion}, " +
                    $"this build speaks {BrokerEndpoint.ProtocolVersion}.");

                return Unavailable(request, "Rättighetstjänsten har en annan version.");
            }

            return response;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException
                                      or OperationCanceledException or UnauthorizedAccessException
                                      or BrokerFrameException or InvalidOperationException)
        {
            // Expected on a machine where the service is not installed, which
            // is every development machine. Not an error worth throwing over:
            // the caller's job is to fail closed, not to crash.
            _logger.Warning(BrokerAudit.Category,
                "The security service could not be reached.", ex);

            Remember(false);

            return Unavailable(request, "Rättighetstjänsten kunde inte nås.");
        }
    }

    /// <summary>
    /// Opens the pipe, as the caller's own identity.
    ///
    /// <see cref="TokenImpersonationLevel.Identification"/> and nothing more.
    /// It lets the service read who is calling - which is the whole basis of
    /// the authorization matrix - and does not let the service act as them.
    /// Impersonation would hand a LocalSystem service the ability to do
    /// things as the child, which it has no reason to want and every reason
    /// not to have.
    /// </summary>
    private NamedPipeClientStream Connect()
    {
        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

        try
        {
            pipe.Connect(BrokerEndpoint.ConnectTimeoutMilliseconds);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static ElevatedResponse Unavailable(ElevatedRequest request, string message) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Rejected = false,
        Reason = BrokerFailureReason.ServiceUnavailable,
        Message = message
    };
}
