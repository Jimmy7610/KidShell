using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;

namespace KidShell.WindowsIntegration.Broker;

/// <summary>
/// The privileged broker's endpoint: one named pipe, served by the service.
///
/// WHAT THIS TYPE IS RESPONSIBLE FOR, AND WHAT IT IS NOT
/// -----------------------------------------------------
/// It accepts connections, establishes WHO connected, reads one framed
/// message, and hands both to <see cref="IElevatedBrokerServer"/>. Every
/// decision about what that caller may do is made there, on rules that are
/// unit-tested without a pipe. This file is the part that cannot be tested
/// without Windows, so it is kept as close to nothing as it can be.
///
/// THE ACCESS LIST IS NOT THE AUTHORIZATION
/// ----------------------------------------
/// The descriptor built here decides who may open the pipe. It does not and
/// cannot decide what they may ask for. Those are different questions and
/// both are answered - see <see cref="BrokerPipeAccessPlan"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeBrokerListener : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly BrokerPipeAccessPlan _plan;
    private readonly IElevatedBrokerServer _server;
    private readonly IKidShellLogger _logger;
    private readonly string _childSid;

    public NamedPipeBrokerListener(
        string pipeName,
        BrokerPipeAccessPlan plan,
        IElevatedBrokerServer server,
        IKidShellLogger logger,
        string childSid = "")
    {
        _pipeName = pipeName;
        _plan = plan;
        _server = server;
        _logger = logger;
        _childSid = childSid;
    }

    /// <summary>
    /// Serves connections until cancelled.
    ///
    /// One connection, one message, one answer, disconnect. A client that
    /// wants to say something else connects again, which costs a handle and
    /// buys a property worth more than the handle: a connection cannot be
    /// held open to keep an identity alive after the caller's token has
    /// changed.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.Info(BrokerAudit.Category,
            $"The security broker is listening on {_pipeName} " +
            $"({(_plan.IsChildScoped ? "scoped to the child account" : "unprovisioned access list")}).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ServeOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad connection must not take the service down. A
                // broker that stops listening after a malformed message is a
                // broker a child can switch off.
                _logger.Error(BrokerAudit.Category, "A broker connection failed.", ex);
            }
        }

        _logger.Info(BrokerAudit.Category, "The security broker has stopped listening.");
    }

    private async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        using var pipe = Create();

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var caller = Resolve(pipe);

            // The timeout is on the exchange, not on the service. A caller
            // that connects and says nothing holds one instance for ten
            // seconds and then stops being this service's problem.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(BrokerEndpoint.IoTimeoutMilliseconds);

            string? line;

            try
            {
                line = await BrokerFraming
                    .ReadAsync(pipe, BrokerEndpoint.MaxRequestBytes, deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (BrokerFrameException ex)
            {
                // Logged here and not answered. The framing was wrong, so
                // there is no request id to answer against and nothing the
                // caller could usefully be told.
                _logger.Warning(BrokerAudit.Category,
                    $"A caller broke the framing rules: {ex.Message}");
                return;
            }

            if (line is null)
            {
                return;
            }

            var response = _server.Handle(line, caller);

            await BrokerFraming
                .WriteAsync(pipe, ElevatedProtocol.Serialize(response),
                    BrokerEndpoint.MaxResponseBytes, deadline.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
    }

    /// <summary>
    /// Creates the pipe with the plan's descriptor.
    ///
    /// <c>NamedPipeServerStreamAcl.Create</c> rather than the ordinary
    /// constructor, because the ordinary constructor gives the pipe a default
    /// descriptor and then there is a window in which it exists without the
    /// one the plan describes.
    /// </summary>
    private NamedPipeServerStream Create() =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            Describe(_plan));

    /// <summary>
    /// Turns the plan into a Windows security descriptor.
    ///
    /// Note what nobody but SYSTEM gets: <c>CreateNewInstance</c>. A
    /// principal holding it can serve this pipe name itself and answer a
    /// client's questions as though it were the service - which for
    /// VerifyParentPin means answering "yes".
    /// </summary>
    internal static PipeSecurity Describe(BrokerPipeAccessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var security = new PipeSecurity();

        foreach (var entry in plan.Entries)
        {
            var identity = IdentityOf(entry);

            if (identity is null)
            {
                continue;
            }

            security.AddAccessRule(
                new PipeAccessRule(identity, RightsOf(entry.Rights), AccessControlType.Allow));
        }

        // The owner is SYSTEM, so nothing short of SYSTEM or an administrator
        // can take ownership and rewrite the list.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));

        return security;
    }

    private static IdentityReference? IdentityOf(BrokerPipeAce entry) => entry.Principal switch
    {
        BrokerPipePrincipal.System =>
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),

        BrokerPipePrincipal.Administrators =>
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),

        BrokerPipePrincipal.AuthenticatedUsers =>
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),

        BrokerPipePrincipal.ChildAccount when entry.Sid.Length > 0 =>
            new SecurityIdentifier(entry.Sid),

        // A child entry with no SID describes nothing, and an entry that
        // describes nothing must not become an entry that describes
        // everyone.
        _ => null
    };

    internal static PipeAccessRights RightsOf(BrokerPipeRights rights)
    {
        var result = (PipeAccessRights)0;

        if (rights.HasFlag(BrokerPipeRights.Connect))
        {
            result |= PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        }

        if (rights.HasFlag(BrokerPipeRights.CreateInstance))
        {
            result |= PipeAccessRights.CreateNewInstance;
        }

        if (rights.HasFlag(BrokerPipeRights.ChangePermissions))
        {
            result |= PipeAccessRights.ChangePermissions | PipeAccessRights.ReadPermissions;
        }

        return result;
    }

    /// <summary>
    /// Establishes who connected, from Windows rather than from the message.
    ///
    /// The SID comes out of the impersonation token on the pipe. Nothing the
    /// caller sends contributes to it, which is the whole difference between
    /// an authorization and a courtesy.
    ///
    /// Elevation is read as membership of the Administrators group in the
    /// token presented. A non-elevated administrator presents a filtered
    /// token in which that membership is deny-only, so this answers false for
    /// them - which is the behaviour wanted: "an administrator who has
    /// consented", not "an account that could consent if asked".
    /// </summary>
    private BrokerCaller Resolve(NamedPipeServerStream pipe)
    {
        try
        {
            BrokerCaller resolved = BrokerCaller.Unknown;

            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                resolved = Classify(identity, SessionIdOf(pipe));
            });

            if (!resolved.IsAuthenticated)
            {
                _logger.Warning(BrokerAudit.Category, "A caller could not be identified and was refused.");
            }

            return resolved;
        }
        catch (Exception ex)
        {
            // Failing to classify is a refusal, not a fallback. An
            // unidentified caller gets BrokerCaller.Unknown, and the matrix
            // refuses Unknown for every operation.
            _logger.Error(BrokerAudit.Category, "A caller's identity could not be established.", ex);
            return BrokerCaller.Unknown;
        }
    }

    private BrokerCaller Classify(WindowsIdentity identity, int sessionId)
    {
        var sid = identity.User?.Value ?? string.Empty;

        if (sid.Length == 0 || !identity.IsAuthenticated)
        {
            return BrokerCaller.Unknown;
        }

        if (identity.IsSystem)
        {
            return new BrokerCaller
            {
                Class = BrokerCallerClass.System,
                Sid = sid,
                AccountName = identity.Name,
                IsElevated = true,
                SessionId = sessionId
            };
        }

        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return new BrokerCaller
            {
                Class = BrokerCallerClass.Administrator,
                Sid = sid,
                AccountName = identity.Name,
                IsElevated = true,
                SessionId = sessionId
            };
        }

        // A provisioned device knows which account the child uses, and
        // nothing else gets to be the child's session. Before provisioning
        // there is no SID to compare against, so any authenticated caller is
        // classified as a session - which grants only the operations the
        // transition rules already make one-directional.
        if (_childSid.Length > 0 && !string.Equals(_childSid, sid, StringComparison.OrdinalIgnoreCase))
        {
            return BrokerCaller.Unknown;
        }

        return new BrokerCaller
        {
            Class = BrokerCallerClass.ChildSession,
            Sid = sid,
            AccountName = identity.Name,
            IsElevated = false,
            SessionId = sessionId
        };
    }

    /// <summary>
    /// The Windows session the client is in, straight from the pipe.
    ///
    /// A capability is bound to it, so it has to come from the operating
    /// system. Zero when Windows will not say, which never matches a session
    /// a capability was issued in unless the caller really is in session
    /// zero.
    /// </summary>
    private static int SessionIdOf(NamedPipeServerStream pipe) =>
        GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? (int)session : 0;

    // DllImport rather than LibraryImport, matching the rest of this
    // assembly. The generated marshalling that LibraryImport produces needs
    // unsafe code, and turning that on for the whole project to read one
    // integer is not a trade worth making.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafeHandle pipe, out uint clientSessionId);

    /// <summary>
    /// How many clients may be connected at once.
    ///
    /// Bounded, because an unbounded listener is a way to exhaust a
    /// LocalSystem service's handles from an unprivileged account. Four is
    /// more than one shell and an approval prompt ever need together.
    /// </summary>
    private const int MaxInstances = 4;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
