using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// Handles one request, from one identified caller.
///
/// Separate from the pipe on purpose. The transport is Windows, the ACL is
/// Windows and the token is Windows; the decisions are not, and they are
/// where every mistake in this area actually lives. So every rule is
/// exercised by tests that pass a caller and a line of JSON, and the pipe is
/// a thin shell around this with nothing to get wrong but plumbing.
/// </summary>
public interface IElevatedBrokerServer
{
    ElevatedResponse Handle(string line, BrokerCaller caller);
}

/// <summary>
/// Operations this server does not own, handed on to the machine-mutation
/// dispatcher.
///
/// Account creation, AppLocker deployment and the rest live in
/// KidShell.WindowsIntegration and cannot be referenced from here. They are
/// still authorized here, before they are handed on, so there is one place
/// that decides who may ask for what.
/// </summary>
public interface IElevatedOperationDispatcher
{
    ElevatedResponse Dispatch(ElevatedRequest request);
}

/// <summary>
/// The privileged broker's decisions.
///
/// THE ORDER IS FIXED AND NEVER VARIES
/// -----------------------------------
///     size -> parse -> protocol -> shape -> AUTHORITY -> transition -> act
///
/// Authority sits in the middle rather than at the end because everything
/// after it is privileged work, and everything before it is free. A caller
/// that fails authorization has not caused the service to read a document, to
/// hash anything, or to touch the disk.
///
/// WHAT THE CHILD IS TOLD WHEN IT FAILS
/// ------------------------------------
/// A <see cref="BrokerFailureReason"/> and a sentence in Swedish. Never an
/// exception, never a path, never which document exists. The detail goes to
/// the service's own log, which the child cannot read.
/// </summary>
public sealed class ElevatedBrokerServer : IElevatedBrokerServer
{
    private readonly IPrivilegedProtectedStore _store;
    private readonly ParentCapabilityRegistry _capabilities;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;
    private readonly IElevatedOperationDispatcher? _machine;
    private readonly PinAttemptThrottle _throttle;
    private readonly Lock _gate = new();

    public ElevatedBrokerServer(
        IPrivilegedProtectedStore store,
        ParentCapabilityRegistry capabilities,
        IKidShellLogger logger,
        TimeProvider? time = null,
        IElevatedOperationDispatcher? machine = null)
    {
        _store = store;
        _capabilities = capabilities;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _machine = machine;
        _throttle = new PinAttemptThrottle(_time);

        RestoreThrottle();
    }

    // ------------------------------------------------------------- entry

    public ElevatedResponse Handle(string line, BrokerCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (line is null)
        {
            return Reject("unknown", BrokerFailureReason.MalformedRequest, "Begäran saknas.");
        }

        // Measured before anything parses it. A caller does not get to make a
        // LocalSystem service allocate its way through an announcement.
        if (Encoding.UTF8.GetByteCount(line) > BrokerEndpoint.MaxRequestBytes)
        {
            Audit(caller, "a request larger than the protocol allows");
            return Reject("unknown", BrokerFailureReason.PayloadTooLarge, "Begäran är för stor.");
        }

        var request = ElevatedProtocol.DeserializeRequest(line);

        if (request is null)
        {
            Audit(caller, "a request that could not be parsed");
            return Reject("unknown", BrokerFailureReason.MalformedRequest, "Begäran kunde inte tolkas.");
        }

        return Handle(request, caller);
    }

    public ElevatedResponse Handle(ElevatedRequest request, BrokerCaller caller)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (request.ProtocolVersion != BrokerEndpoint.ProtocolVersion)
        {
            Audit(caller, $"protocol {request.ProtocolVersion}");
            return Reject(SafeId(request), BrokerFailureReason.UnsupportedProtocol,
                "Begäran använder ett protokoll som inte stöds.");
        }

        if (!Enum.IsDefined(request.Kind))
        {
            Audit(caller, "an operation this build does not have");
            return Reject(SafeId(request), BrokerFailureReason.UnknownOperation, "Okänd åtgärd.");
        }

        if (ElevatedRequestValidator.Validate(request) is { } shape)
        {
            Audit(caller, $"a malformed {request.Kind} request");
            return Reject(SafeId(request), BrokerFailureReason.PayloadInvalid, shape);
        }

        // ---------------------------------------------------- authority

        var hasCapability = _capabilities.IsValid(request.ParentCapability, caller);
        var decision = BrokerAuthorizationPolicy.Decide(caller, request.Kind, hasCapability);

        if (!decision.Allowed)
        {
            _logger.Warning(BrokerAudit.Category,
                $"Refused {request.Kind} from {Describe(caller)}: {decision.Explanation}");

            return Reject(SafeId(request), decision.Reason, MessageFor(decision.Reason));
        }

        var authority = hasCapability && caller.Class != BrokerCallerClass.ChildSession
            ? BrokerAuthority.Administrator
            : hasCapability
                ? BrokerAuthority.ParentCapability
                : caller.Class is BrokerCallerClass.Administrator or BrokerCallerClass.System
                    ? BrokerAuthority.Administrator
                    : BrokerAuthority.ChildSessionRestricted;

        lock (_gate)
        {
            return Act(request, caller, authority);
        }
    }

    // ------------------------------------------------------------- acting

    private ElevatedResponse Act(ElevatedRequest request, BrokerCaller caller, BrokerAuthority authority) =>
        request.Kind switch
        {
            ElevatedOperationKind.Probe => Ok(request, "Rättighetshjälparen svarar."),

            ElevatedOperationKind.SaveScreenTimeState => SaveScreenTime(request, authority),
            ElevatedOperationKind.SavePinThrottleState => SavePinThrottle(request, authority),

            ElevatedOperationKind.StageParentPolicy => Stage(request),
            ElevatedOperationKind.CommitStagedParentPolicy => Commit(request),

            ElevatedOperationKind.SaveParentPolicy => WriteDirect(request, ProtectedDocument.ParentPolicy),
            ElevatedOperationKind.MarkProvisioned => WriteDirect(request, ProtectedDocument.ProvisioningMarker),

            ElevatedOperationKind.VerifyParentPin => VerifyPin(request, caller),
            ElevatedOperationKind.GrantScreenTime => Grant(request),
            ElevatedOperationKind.ResetScreenTimeToday => ResetToday(request),

            // Machine mutations. Authorized above, performed elsewhere, and
            // refused outright when this host has no dispatcher - which is
            // every host that is not the security service.
            _ => _machine?.Dispatch(request)
                 ?? Reject(SafeId(request), BrokerFailureReason.UnknownOperation, "Åtgärden stöds inte här.")
        };

    // ------------------------------------------------- enforcement state

    private ElevatedResponse SaveScreenTime(ElevatedRequest request, BrokerAuthority authority)
    {
        var current = ProtectedStateTransitionRules.Parse<ScreenTimeState>(
            _store.Read(ProtectedDocument.ScreenTimeState));

        var proposed = ProtectedStateTransitionRules.Parse<ScreenTimeState>(request.ProtectedPayload);

        var decision = ProtectedStateTransitionRules.ScreenTime(current, proposed, authority);

        if (!decision.Allowed)
        {
            _logger.Warning(BrokerAudit.Category,
                $"Refused a screen-time write: {decision.Explanation}");

            return Reject(SafeId(request), decision.Reason,
                "Den föreslagna skärmtiden godtogs inte.");
        }

        return Persist(request, ProtectedDocument.ScreenTimeState, request.ProtectedPayload!);
    }

    private ElevatedResponse SavePinThrottle(ElevatedRequest request, BrokerAuthority authority)
    {
        var current = ProtectedStateTransitionRules.Parse<PinThrottleState>(
            _store.Read(ProtectedDocument.PinThrottleState));

        var proposed = ProtectedStateTransitionRules.Parse<PinThrottleState>(request.ProtectedPayload);

        var decision = ProtectedStateTransitionRules.PinThrottle(
            current, proposed, authority, _time.GetUtcNow());

        if (!decision.Allowed)
        {
            _logger.Warning(BrokerAudit.Category, $"Refused a throttle write: {decision.Explanation}");

            return Reject(SafeId(request), decision.Reason, "Den föreslagna spärren godtogs inte.");
        }

        var written = Persist(request, ProtectedDocument.PinThrottleState, request.ProtectedPayload!);

        if (written.Success && proposed is not null)
        {
            // Keep the service's own throttle in step, so a restart of the
            // child process cannot desynchronise the two.
            _throttle.Restore(proposed.FailedAttemptCount, proposed.CooldownUntilUtc);
        }

        return written;
    }

    // -------------------------------------------------- parent policy

    private ElevatedResponse Stage(ElevatedRequest request)
    {
        var write = _store.WriteStaged(request.ProtectedPayload!);

        if (!write.Success)
        {
            return Reject(SafeId(request), BrokerFailureReason.StorageFailed, "Förslaget kunde inte sparas.");
        }

        var digest = Digest(request.ProtectedPayload!);

        _logger.Info(BrokerAudit.Category, $"A parent policy was staged for approval ({digest[..12]}).");

        return new ElevatedResponse
        {
            RequestId = SafeId(request),
            Success = true,
            Message = "Förslaget väntar på godkännande.",
            StagedDigest = digest
        };
    }

    private ElevatedResponse Commit(ElevatedRequest request)
    {
        var staged = _store.ReadStaged();

        if (string.IsNullOrWhiteSpace(staged))
        {
            return Reject(SafeId(request), BrokerFailureReason.NothingStaged,
                "Det finns inget förslag att godkänna.");
        }

        var actual = Digest(staged);

        // What the approver was shown, against what is about to become live.
        // Without this, a child's session could stage an innocuous policy,
        // wait for the prompt, and replace it while the parent reads the
        // consent dialog.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(request.ExpectedDigest!)))
        {
            _logger.Error(BrokerAudit.Category,
                "A staged policy changed between approval and commit. Nothing was applied.");

            return Reject(SafeId(request), BrokerFailureReason.NothingStaged,
                "Förslaget har ändrats sedan det visades. Ingenting har sparats.");
        }

        if (ProtectedPayloadPolicy.Validate(staged) is { } problem)
        {
            return Reject(SafeId(request), BrokerFailureReason.PayloadInvalid, problem);
        }

        var write = _store.Write(ProtectedDocument.ParentPolicy, staged);

        if (!write.Success)
        {
            return Reject(SafeId(request), BrokerFailureReason.StorageFailed, "Kunde inte sparas.");
        }

        // Provisioning is an administrator-approved transition, not a child
        // operation. The child may stage a first policy, but only this
        // elevated commit is allowed to turn "never set up" into
        // "provisioned". Keeping the marker here preserves the authorization
        // matrix while ensuring a successful first onboarding does not leave
        // the machine looking unprovisioned.
        if (string.IsNullOrWhiteSpace(_store.Read(ProtectedDocument.ProvisioningMarker)))
        {
            var marker = _store.Write(
                ProtectedDocument.ProvisioningMarker,
                ProtectedPolicyTrustEvaluator.MarkerDocument(_time.GetUtcNow()));

            if (!marker.Success)
            {
                _logger.Error(BrokerAudit.Category,
                    "The parent policy was committed, but the provisioning marker could not be written.");

                return Reject(SafeId(request), BrokerFailureReason.StorageFailed,
                    "Policyn sparades men enheten kunde inte markeras som konfigurerad.");
            }
        }

        _store.ClearStaged();

        // The rules changed, so every authority that was granted under the
        // old ones stops. A parent who changes the PIN has ended the sessions
        // that were unlocked with the previous one.
        _capabilities.RevokeAll();

        _logger.Info(BrokerAudit.Category, "A staged parent policy was approved and is now live.");

        return Ok(request, "Sparat.");
    }

    private ElevatedResponse WriteDirect(ElevatedRequest request, ProtectedDocument document) =>
        Persist(request, document, request.ProtectedPayload!);

    // --------------------------------------------------------- parent pin

    /// <summary>
    /// Verifies a PIN where verifying it means something.
    ///
    /// The comparison, the failure count and the cooldown are all on this
    /// side. A child's process can ask, and can be told no; it cannot decide
    /// that the answer was yes, and it cannot forget that it asked.
    /// </summary>
    private ElevatedResponse VerifyPin(ElevatedRequest request, BrokerCaller caller)
    {
        var allowed = _throttle.Evaluate();

        if (!allowed.IsAllowed)
        {
            return new ElevatedResponse
            {
                RequestId = SafeId(request),
                Success = false,
                Rejected = true,
                Reason = BrokerFailureReason.PinThrottled,
                Message = "För många försök. Försök igen om en stund.",
                RetryAfterSeconds = (int)Math.Ceiling(allowed.RetryAfter.TotalSeconds)
            };
        }

        var policy = ProtectedStateTransitionRules.Parse<ParentPolicyDocument>(
            _store.Read(ProtectedDocument.ParentPolicy));

        var pin = policy?.ParentPin;

        if (pin is null || !pin.IsConfigured)
        {
            // No PIN has been set. The service does not invent one, and it
            // does not treat "none" as "anything matches".
            _logger.Warning(BrokerAudit.Category, "A PIN was offered and no PIN is configured.");
            return RecordFailure(request, BrokerFailureReason.PinIncorrect);
        }

        if (!PinHasher.IsAcceptableIterationCount(pin.Iterations))
        {
            // The iteration count comes off disk, so it is untrusted input in
            // both directions: too low weakens the hash, too high is a way to
            // make a LocalSystem service burn a core on every attempt.
            _logger.Error(BrokerAudit.Category,
                "The stored PIN iteration count is outside the accepted range.");

            return RecordFailure(request, BrokerFailureReason.PinIncorrect);
        }

        // IsConfigured has already established both are present; the local
        // copies are what make that visible to the compiler as well as to a
        // reader.
        var hash = pin.Hash!;
        var salt = pin.Salt!;

        var correct = PinHasher.Verify(request.ParentPinAttempt!, hash, salt, pin.Iterations);

        if (!correct)
        {
            return RecordFailure(request, BrokerFailureReason.PinIncorrect);
        }

        _throttle.RecordSuccess();
        PersistThrottle();

        var token = _capabilities.Issue(caller);

        _logger.Info(BrokerAudit.Category, $"A parent authenticated from {Describe(caller)}.");

        return new ElevatedResponse
        {
            RequestId = SafeId(request),
            Success = true,
            Message = "Rätt kod.",
            ParentCapability = token,
            CapabilitySeconds = (int)_capabilities.Lifetime.TotalSeconds
        };
    }

    private ElevatedResponse RecordFailure(ElevatedRequest request, BrokerFailureReason reason)
    {
        var decision = _throttle.RecordFailure();
        PersistThrottle();

        return new ElevatedResponse
        {
            RequestId = SafeId(request),
            Success = false,
            Rejected = true,
            Reason = decision.IsAllowed ? reason : BrokerFailureReason.PinThrottled,
            Message = decision.IsAllowed ? "Fel kod." : "För många försök. Försök igen om en stund.",
            RetryAfterSeconds = (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)
        };
    }

    // ------------------------------------------------------ parent grants

    /// <summary>
    /// Applies a grant to the counter the service holds.
    ///
    /// The caller says how many minutes, never what the resulting state is.
    /// That is the difference between a grant and a write, and it is why
    /// grants did not stay inside SaveScreenTimeState: a state the caller
    /// composes is a state the caller chose, and "used seconds: 0" is a
    /// perfectly well-formed one.
    /// </summary>
    private ElevatedResponse Grant(ElevatedRequest request)
    {
        var current = ProtectedStateTransitionRules.Parse<ScreenTimeState>(
            _store.Read(ProtectedDocument.ScreenTimeState)) ?? Fresh();

        if (request.GrantRestOfDay)
        {
            current.UnlimitedForToday = true;
        }
        else
        {
            current.BonusMinutes = Math.Min(
                ProtectedStateTransitionRules.MaxBonusMinutes,
                current.BonusMinutes + request.GrantMinutes);
        }

        current.Sequence++;
        current.LastUpdatedUtc = _time.GetUtcNow();

        _logger.Info(BrokerAudit.Category, request.GrantRestOfDay
            ? "A parent lifted today's screen-time limit."
            : $"A parent granted {request.GrantMinutes} extra minutes.");

        return Persist(request, ProtectedDocument.ScreenTimeState, Serialize(current));
    }

    /// <summary>
    /// Clears today's counter, on the privileged side.
    ///
    /// The one operation that may lower used time, which is exactly why a
    /// child's session cannot reach it. What survives: the clock-event count,
    /// because it is evidence about the machine rather than about today.
    /// </summary>
    private ElevatedResponse ResetToday(ElevatedRequest request)
    {
        var current = ProtectedStateTransitionRules.Parse<ScreenTimeState>(
            _store.Read(ProtectedDocument.ScreenTimeState));

        var reset = Fresh();
        reset.SuspiciousClockEvents = current?.SuspiciousClockEvents ?? 0;
        reset.Sequence = (current?.Sequence ?? 0) + 1;
        reset.SessionState = ScreenTimeSessionState.Open;

        _logger.Info(BrokerAudit.Category, "A parent reset today's screen-time counter.");

        return Persist(request, ProtectedDocument.ScreenTimeState, Serialize(reset));
    }

    private ScreenTimeState Fresh() => new()
    {
        LocalDate = _time.GetLocalNow().ToString("yyyy-MM-dd"),
        LastUpdatedUtc = _time.GetUtcNow()
    };

    // -------------------------------------------------------- throttle io

    /// <summary>
    /// Picks the throttle up where the last run of the service left it.
    ///
    /// Without this, restarting the service would forgive every failure, and
    /// a child who can make a service restart has found the same hole the
    /// in-memory throttle had.
    /// </summary>
    private void RestoreThrottle()
    {
        var stored = ProtectedStateTransitionRules.Parse<PinThrottleState>(
            _store.Read(ProtectedDocument.PinThrottleState));

        if (stored is not null)
        {
            _throttle.Restore(stored.FailedAttemptCount, stored.CooldownUntilUtc);
        }
    }

    private void PersistThrottle()
    {
        var (failures, until) = _throttle.Snapshot();

        var state = new PinThrottleState
        {
            FailedAttemptCount = failures,
            CooldownUntilUtc = until,
            LastFailureUtc = failures > 0 ? _time.GetUtcNow() : null
        };

        var write = _store.Write(ProtectedDocument.PinThrottleState, Serialize(state));

        if (!write.Success)
        {
            _logger.Warning(BrokerAudit.Category,
                $"The PIN throttle could not be persisted ({write.Status}).");
        }
    }

    // ------------------------------------------------------------ helpers

    private ElevatedResponse Persist(ElevatedRequest request, ProtectedDocument document, string json)
    {
        var write = _store.Write(document, json);

        if (write.Success)
        {
            return Ok(request, "Sparat.");
        }

        // The status crosses back; the detail does not. "Access denied on
        // C:\ProgramData\KidShell\policy" is a sentence that tells the child
        // something it is not supposed to know.
        _logger.Error(BrokerAudit.Category,
            $"Protected write of {document} failed: {write.Status} {write.Detail}");

        return Reject(SafeId(request),
            write.Status == ProtectedWriteStatus.Rejected
                ? BrokerFailureReason.PayloadInvalid
                : BrokerFailureReason.StorageFailed,
            "Kunde inte sparas.");
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ProtectedStateTransitionRules.DocumentOptions);

    /// <summary>
    /// The staged document's digest.
    ///
    /// Delegated rather than recomputed: the side that stages and the side
    /// that approves must agree byte for byte, and two definitions of "the
    /// digest" is a comparison that silently never matches.
    /// </summary>
    public static string Digest(string content) => ProtectedDocumentNames.DigestOf(content);

    private static ElevatedResponse Ok(ElevatedRequest request, string message) => new()
    {
        RequestId = SafeId(request),
        Success = true,
        Message = message
    };

    private static ElevatedResponse Reject(string id, BrokerFailureReason reason, string message) =>
        ElevatedResponse.Reject(id, reason, message);

    /// <summary>
    /// The request id, bounded.
    ///
    /// It is echoed back and written to the log, so it is treated as what it
    /// is: a string a caller chose.
    /// </summary>
    private static string SafeId(ElevatedRequest request)
    {
        var id = request.RequestId;

        if (string.IsNullOrEmpty(id))
        {
            return "unknown";
        }

        var clean = new string(id.Where(c => !char.IsControl(c)).Take(64).ToArray());

        return clean.Length == 0 ? "unknown" : clean;
    }

    private static string MessageFor(BrokerFailureReason reason) => reason switch
    {
        BrokerFailureReason.ParentAuthorizationRequired => "En vuxen behöver godkänna det här.",
        BrokerFailureReason.UnknownOperation => "Okänd åtgärd.",
        _ => "Åtgärden är inte tillåten."
    };

    private void Audit(BrokerCaller caller, string what) =>
        _logger.Warning(BrokerAudit.Category, $"Refused {what} from {Describe(caller)}.");

    private static string Describe(BrokerCaller caller) =>
        $"{caller.Class} {caller.Sid} (session {caller.SessionId})";
}

/// <summary>The log category every broker decision is recorded under.</summary>
public static class BrokerAudit
{
    public const string Category = "Broker";
}
