using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;
using KidShell.WindowsIntegration.Platform;

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
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once a pipe instance exists and is waiting for a caller.
    ///
    /// LIFECYCLE INFORMATION, NOT A SECURITY DECISION
    /// ----------------------------------------------
    /// Nothing in the broker consults this. It does not gate a request, relax
    /// a check or stand in for one: it answers "is the endpoint up", which a
    /// test needs to know and an attacker gains nothing from.
    ///
    /// It completes only AFTER <see cref="Create"/> has returned a pipe. If
    /// creation throws - the name taken, the descriptor refused - it does not
    /// complete, because reporting readiness for an endpoint that does not
    /// exist is the failure mode the first version of this listener actually
    /// had: it caught the creation exception, retried forever, and would have
    /// looked like a running service that served nothing.
    ///
    /// On a clean shutdown it is cancelled rather than left hanging, so a
    /// caller awaiting it is not stuck once the listener has stopped.
    /// </summary>
    public Task Ready => _ready.Task;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Before the first connection rather than during it, so the first
        // caller after a service start is not the one that pays for it - and
        // is not the one that gets refused if it goes wrong.
        WarmUpIdentity();

        _logger.Info(BrokerAudit.Category,
            $"The security broker is listening on {_pipeName} " +
            $"({(_plan.IsChildScoped ? "scoped to the child account" : "unprovisioned access list")}).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ServeOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Only OUR cancellation ends the loop.
                //
                // This used to catch every OperationCanceledException,
                // including the per-exchange deadline - so a caller that
                // connected and said nothing for ten seconds stopped the
                // broker permanently. One connect, and the parent's policy
                // could not be written again until the service restarted.
                // The product fails closed on that, which is the only reason
                // it was not worse.
                break;
            }
            catch (Exception ex)
            {
                // One bad connection must not take the service down. A
                // broker that stops listening after a malformed message is a
                // broker a child can switch off.
                _logger.Error(BrokerAudit.Category, "A broker connection failed.", ex);

                // But a failure that repeats - the pipe name taken, the
                // descriptor refused - must not become a spin. Without this
                // pause a service that cannot create its endpoint would burn
                // a core logging the same line, which is a worse failure
                // than not starting.
                try
                {
                    await Task.Delay(RetryPauseMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Nobody is going to become ready now. A test or a caller awaiting
        // readiness gets an answer rather than hanging until its own timeout.
        _ready.TrySetCanceled(CancellationToken.None);

        _logger.Info(BrokerAudit.Category, "The security broker has stopped listening.");
    }

    private async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        using var pipe = Create();

        // Signalled here and not a line earlier. The pipe object exists, so a
        // client can open it; the first caller of RunAsync is therefore safe
        // to proceed. Deliberately after Create and before the wait, because
        // "ready" means the endpoint is there, and a server that has not yet
        // reached WaitForConnectionAsync still accepts a connection - Windows
        // leaves it pending.
        _ready.TrySetResult();

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The timeout is on the exchange, not on the service. A caller that
        // connects and says nothing holds one instance for ten seconds and
        // then stops being this service's problem.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(BrokerEndpoint.IoTimeoutMilliseconds);

        string? line;

        try
        {
            // READ FIRST, THEN IDENTIFY.
            //
            // Not a preference. ImpersonateNamedPipeClient cannot establish
            // who the caller is until the caller has written something, so a
            // server that identifies before reading either fails to identify
            // or - with a small pipe buffer - waits for a write that is
            // itself waiting for this read.
            //
            // Reading first costs nothing in safety. The length prefix is
            // checked against the protocol limit before a byte is allocated,
            // and the access list has already decided who may be on the
            // other end at all. What the message MEANS is not looked at
            // until the caller is known.
            line = await BrokerFraming
                .ReadAsync(pipe, BrokerEndpoint.MaxRequestBytes, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (BrokerFrameException ex)
        {
            // Logged here and not answered. The framing was wrong, so there
            // is no request id to answer against and nothing the caller
            // could usefully be told.
            _logger.Warning(BrokerAudit.Category,
                $"A caller broke the framing rules: {ex.Message}");
            return;
        }

        if (line is null)
        {
            return;
        }

        var caller = Resolve(pipe);
        var response = _server.Handle(line, caller);

        await BrokerFraming
            .WriteAsync(pipe, ElevatedProtocol.Serialize(response),
                BrokerEndpoint.MaxResponseBytes, deadline.Token)
            .ConfigureAwait(false);

        // NO Disconnect(). THIS WAS THE CI FAILURE.
        //
        // DisconnectNamedPipe forces the client off and DISCARDS anything in
        // the pipe the client has not read yet. The reply had just been
        // written, so on a machine where the client's read continuation was
        // slower than this line - a two-core runner with a contended thread
        // pool - the answer was thrown away and the client saw a clean
        // end-of-stream. It reported "the service did not respond", which is
        // indistinguishable from the service not being there, so a refusal
        // the broker had correctly decided came back as ServiceUnavailable.
        //
        // Measured, not guessed: with Disconnect() and a 50ms delay before
        // the client reads, the reply is lost every time; with the handle
        // simply closed, it arrives every time. Closing is the graceful path
        // - the client drains what is buffered and then sees the end - and
        // the loop creates a fresh instance for the next caller anyway, so
        // Disconnect bought nothing to begin with.
        //
        // WaitForPipeDrain would be the other way to be sure, and is worse
        // here: it blocks until the client reads, it takes no cancellation
        // token, and a caller that connects and never reads would hold the
        // serve loop.
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
            // Bounded, and not zero. A zero-byte buffer makes every write
            // block until the other side reads, which turns an ordinary
            // request into a lock-step exchange and leaves no room for the
            // two sides to be wrong about the order. Bounded because the
            // memory belongs to a LocalSystem service and the number of
            // instances is small.
            inBufferSize: BufferBytes,
            outBufferSize: BufferBytes,
            Describe(_plan));

    /// <summary>Comfortably more than any message this protocol sends.</summary>
    private const int BufferBytes = 64 * 1024;

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

        // THE OWNER IS NOT SET HERE, AND THAT IS NOT AN OVERSIGHT.
        //
        // The first version did set it, to LocalSystem, on the reasoning
        // that an owner can always rewrite the access list. Windows refuses:
        // a process may only name an owner it is entitled to, so any creator
        // that is not already SYSTEM gets "this security ID may not be
        // assigned as the owner of this object" and the pipe is never
        // created at all. The listener would have caught that exception and
        // retried forever, so the service would have started, logged, and
        // served nothing.
        //
        // The creator owns the object by default, and in production the
        // creator is the LocalSystem service. The property is therefore
        // true by construction rather than by assertion, which is the only
        // way it can be true at all.
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
    /// Forces the identity types to load BEFORE anyone impersonates.
    ///
    /// WHY THIS IS NOT SUPERSTITION
    /// ----------------------------
    /// <c>RunAsClient</c> impersonates the caller, and the caller connects at
    /// <see cref="TokenImpersonationLevel.Identification"/> - deliberately,
    /// because the service must be able to read who is calling and must not
    /// be able to act as them. An identification-level token cannot be used
    /// for file access at all, so any assembly the runtime has not loaded YET
    /// cannot be loaded while that impersonation is in effect.
    ///
    /// <c>WindowsIdentity</c> derives from <c>ClaimsIdentity</c>, so the
    /// first call to <c>WindowsIdentity.GetCurrent()</c> pulls in
    /// System.Security.Claims. Inside the impersonated block that load fails,
    /// and the failure arrives as a bare FileNotFoundException naming an
    /// assembly that is plainly present - so the caller cannot be identified
    /// and the request is refused.
    ///
    /// It is invisible most of the time: anything else that has already
    /// touched a Windows identity has loaded the assembly, and then the
    /// service works. Which means it would have failed on the FIRST request
    /// after a service start, every boot, and worked on every one after it.
    /// A security boundary that refuses the first caller and then stops
    /// refusing is the worst kind of intermittent.
    ///
    /// Running the same calls once, unimpersonated, loads what is needed and
    /// makes the impersonated path allocation-only.
    /// </summary>
    private static void WarmUpIdentity()
    {
        if (Volatile.Read(ref _identityWarm))
        {
            return;
        }

        using var current = WindowsIdentity.GetCurrent();

        // Exactly what Classify does, so exactly the same code paths are
        // loaded and jitted: the identity, the principal, the role check and
        // a well-known SID.
        _ = current.User?.Value;
        _ = current.IsSystem;
        _ = current.IsAuthenticated;
        _ = new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
        _ = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;

        Volatile.Write(ref _identityWarm, true);
    }

    private static bool _identityWarm;

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

            // Everything this needs is already loaded. See WarmUpIdentity.
            WarmUpIdentity();

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
        NamedPipeClientFacts.SessionIdOf(pipe.SafePipeHandle);

    /// <summary>
    /// How many clients may be connected at once.
    ///
    /// Bounded, because an unbounded listener is a way to exhaust a
    /// LocalSystem service's handles from an unprivileged account. Four is
    /// more than one shell and an approval prompt ever need together.
    /// </summary>
    private const int MaxInstances = 4;

    /// <summary>How long to wait before trying again after a failed accept.</summary>
    private const int RetryPauseMilliseconds = 1_000;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
