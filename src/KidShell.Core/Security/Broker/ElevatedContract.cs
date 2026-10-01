using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// The complete set of things the elevated helper will do.
///
/// A CLOSED ENUM IS THE SECURITY BOUNDARY
/// --------------------------------------
/// The helper runs as an administrator. Whatever it accepts, it accepts with
/// those rights, so the only safe contract is one where the set of possible
/// actions is fixed at compile time and the caller chooses from it. There is no
/// "run command", no "execute script", no path to PowerShell, and no member
/// that takes an arbitrary string to evaluate.
///
/// The unelevated KidShell UI can therefore ask for exactly these nine things
/// and nothing else. A compromised or buggy UI cannot turn the helper into a
/// general-purpose administrator shell, because the helper has no vocabulary
/// for it.
/// </summary>
public enum ElevatedOperationKind
{
    /// <summary>Read machine state. The only non-mutating member.</summary>
    Probe = 0,

    CreateChildAccount = 1,
    DemoteChildAccount = 2,
    ConfigureAutostart = 3,
    DeployAppLockerPolicy = 4,
    ConfigureApplicationIdentityService = 5,
    ConfigureAssignedAccess = 6,
    DeployBrowserPolicy = 7,
    InstallWatchdogService = 8,

    // ---------------------------------------------- protected state writes
    //
    // OPSV RETEST 2, FINDING 01. The child process reads its own policy and
    // must not write it, so it asks for these instead. Each one names a
    // DOCUMENT rather than a path: the helper owns the mapping from document
    // to location, because the caller is the unprivileged side.
    //
    // There is deliberately no member that takes a destination. A
    // "write these bytes to this path as an administrator" operation would
    // make everything else in this enum decorative.

    SaveParentPolicy = 9,
    SaveScreenTimeState = 10,
    SavePinThrottleState = 11,
    MarkProvisioned = 12,

    // -------------------------------------------- authority-split additions
    //
    // PRIVILEGED BROKER HARDENING. The four members above were written as
    // though "the request came from KidShell.App" were an authority. It is
    // not: KidShell.App runs as the child, in the child's session, under the
    // child's token, and a modified copy of it is indistinguishable from the
    // real one without code signing that does not exist yet.
    //
    // So the operations are split by the authority they actually need. The
    // child's session may advance enforcement state in the stricter
    // direction and may STAGE a policy change. Turning a staged change into
    // the live policy needs an administrator.

    /// <summary>
    /// Offers a parent policy for approval. Does not change the live one.
    ///
    /// Writes to a slot the service owns, which nothing reads for
    /// enforcement. The child's authority extends to proposing.
    /// </summary>
    StageParentPolicy = 13,

    /// <summary>
    /// Makes the staged policy live. Administrator only.
    ///
    /// Carries the digest the approver saw, so the staged document cannot be
    /// swapped between the moment a parent reviews it and the moment the
    /// prompt is answered.
    /// </summary>
    CommitStagedParentPolicy = 14,

    /// <summary>
    /// Checks a parent's PIN on the privileged side and issues a capability.
    ///
    /// The verification has to happen here. A throttle enforced by the
    /// process being throttled is a suggestion, and a comparison performed by
    /// the child's process is one the child's process can decide the answer
    /// to.
    /// </summary>
    VerifyParentPin = 15,

    /// <summary>Grants bonus minutes or the rest of today. Needs a capability.</summary>
    GrantScreenTime = 16,

    /// <summary>Clears today's counter. Needs a capability.</summary>
    ResetScreenTimeToday = 17,

    /// <summary>Installs or repairs the broker service itself. Administrator only.</summary>
    InstallSecurityHostService = 18
}

/// <summary>
/// One typed request to the elevated helper.
///
/// Every field is a value the helper validates before use. Nothing here is
/// interpreted as a command, a path to execute, or a script.
/// </summary>
public sealed record ElevatedRequest
{
    /// <summary>
    /// The protocol this message is written in.
    ///
    /// Checked before anything else. A LocalSystem service that reads an
    /// unfamiliar message shape and does its best with it is no longer a
    /// boundary, so a mismatch is a refusal.
    /// </summary>
    public int ProtocolVersion { get; init; } = BrokerEndpoint.ProtocolVersion;

    public required ElevatedOperationKind Kind { get; init; }

    /// <summary>Correlates the response, and appears in the audit log.</summary>
    public required string RequestId { get; init; }

    /// <summary>The transaction this belongs to, for the recovery manifest.</summary>
    public string TransactionId { get; init; } = string.Empty;

    // ------------------------------------------------------------- accounts

    /// <summary>Local account name. Validated against the Windows rules.</summary>
    public string? Username { get; init; }

    public string? FullName { get; init; }

    /// <summary>
    /// The child account's SID.
    ///
    /// Note what is NOT here: a password field. A credential must not travel
    /// through an IPC channel, be logged, or reach the recovery manifest. The
    /// helper prompts for one itself when a password is genuinely required.
    /// </summary>
    public string? Sid { get; init; }

    // ------------------------------------------------------------ artifacts

    /// <summary>Generated AppLocker policy XML. Validated before deployment.</summary>
    public string? PolicyXml { get; init; }

    /// <summary>Generated Assigned Access configuration XML.</summary>
    public string? ConfigurationXml { get; init; }

    /// <summary>Serialised browser policy settings.</summary>
    public string? BrowserPolicyJson { get; init; }

    /// <summary>KidShell's AUMID, for the autostart value.</summary>
    public string? Aumid { get; init; }

    /// <summary>Full path to the watchdog executable.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// A protected document's contents, for the SaveX operations.
    ///
    /// Contents only. Which document it is comes from <see cref="Kind"/>, and
    /// where it lands is the helper's business - the caller is the
    /// unprivileged side and never names a destination.
    /// </summary>
    public string? ProtectedPayload { get; init; }

    // --------------------------------------------------- parent authority

    /// <summary>
    /// A capability the service issued after it verified a parent's PIN.
    ///
    /// An opaque value the SERVICE generated, holds, and matches against the
    /// caller's SID and session. It is not a claim: a caller cannot make one
    /// up, because the service compares it against what it issued rather
    /// than inspecting it.
    /// </summary>
    public string? ParentCapability { get; init; }

    /// <summary>
    /// A parent's PIN, on its way to the only side that may check it.
    ///
    /// WHY A SECRET IS IN THIS RECORD AT ALL
    /// -------------------------------------
    /// The sibling comment on <see cref="Sid"/> says a credential must not
    /// travel through this channel, and for an account password that is
    /// still right - the helper prompts for one itself.
    ///
    /// The parent's PIN is different, and the difference is who must do the
    /// comparing. A throttle that the throttled process enforces is a
    /// suggestion; a hash comparison the child's process performs is one the
    /// child's process can decide the answer to. For the PIN to mean
    /// anything the verifier has to be the privileged side, so the PIN has to
    /// reach it.
    ///
    /// It costs nothing that was not already exposed: the parent typed it
    /// into this process. It is never logged, never written to the protected
    /// store, never echoed in a response, and never kept after the
    /// comparison.
    /// </summary>
    [JsonPropertyName("pin")]
    public string? ParentPinAttempt { get; init; }

    /// <summary>Bonus minutes to grant. Zero when granting the rest of today.</summary>
    public int GrantMinutes { get; init; }

    /// <summary>Whether the grant lifts today's limit entirely.</summary>
    public bool GrantRestOfDay { get; init; }

    /// <summary>
    /// The SHA-256 of the staged document the approver was shown.
    ///
    /// Carried on a commit so that what an administrator approved and what
    /// becomes live are provably the same bytes.
    /// </summary>
    public string? ExpectedDigest { get; init; }

    /// <summary>Whether the helper should apply, or only report what it would do.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// The request with every secret removed.
    ///
    /// Used wherever a request is logged. There is no code path that writes
    /// an <see cref="ElevatedRequest"/> to a log without going through this,
    /// and a test asserts the PIN does not survive it.
    /// </summary>
    public ElevatedRequest Redacted() => this with
    {
        ParentPinAttempt = null,
        ParentCapability = null,
        ProtectedPayload = ProtectedPayload is null ? null : "<redacted>"
    };
}

/// <summary>
/// Why a request was refused, as a value the unprivileged side may see.
///
/// A CLOSED SET, NOT A STRING FROM THE PRIVILEGED SIDE
/// ---------------------------------------------------
/// Everything the service knows that the child does not is a thing the child
/// would like to learn: a path that exists, an exception type that reveals
/// which call failed, the state of a document it cannot read. So the reason
/// crossing back is chosen from this list, and the detail stays in the
/// service's own log.
/// </summary>
public enum BrokerFailureReason
{
    None = 0,
    MalformedRequest = 1,
    UnsupportedProtocol = 2,
    UnknownOperation = 3,
    PayloadTooLarge = 4,
    PayloadInvalid = 5,

    /// <summary>The caller's Windows identity does not permit this operation.</summary>
    NotAuthorized = 6,

    /// <summary>The operation needs a parent capability and none was valid.</summary>
    ParentAuthorizationRequired = 7,

    /// <summary>The proposed state would weaken what is already recorded.</summary>
    TransitionRejected = 8,

    /// <summary>The PIN was wrong.</summary>
    PinIncorrect = 9,

    /// <summary>Too many failures. The service is holding the caller off.</summary>
    PinThrottled = 10,

    /// <summary>Nothing is staged, or what is staged is not what was approved.</summary>
    NothingStaged = 11,

    /// <summary>The write itself failed on the privileged side.</summary>
    StorageFailed = 12,

    /// <summary>The service could not be reached at all.</summary>
    ServiceUnavailable = 13
}

/// <summary>What the helper did, or refused to do.</summary>
public sealed record ElevatedResponse
{
    public int ProtocolVersion { get; init; } = BrokerEndpoint.ProtocolVersion;

    public required string RequestId { get; init; }

    public required bool Success { get; init; }

    /// <summary>Parent-facing, Swedish, never an HRESULT.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Technical detail for the log. Never shown to a child.
    ///
    /// Populated by the UNPRIVILEGED side's own logging only. The service
    /// leaves it null on anything it sends across the pipe - see
    /// <see cref="BrokerFailureReason"/>.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>Set when the request was rejected before anything ran.</summary>
    public bool Rejected { get; init; }

    /// <summary>Which closed reason applies, when the request did not succeed.</summary>
    public BrokerFailureReason Reason { get; init; } = BrokerFailureReason.None;

    /// <summary>
    /// A capability, when the service has just verified a parent's PIN.
    ///
    /// The only response field that carries authority, and the service
    /// issues it only to the caller it verified, bound to that caller's SID
    /// and session.
    /// </summary>
    public string? ParentCapability { get; init; }

    /// <summary>How long the capability lasts, in seconds.</summary>
    public int CapabilitySeconds { get; init; }

    /// <summary>The SHA-256 of what is currently staged, after a stage.</summary>
    public string? StagedDigest { get; init; }

    /// <summary>How long the caller must wait, when it is being throttled.</summary>
    public int RetryAfterSeconds { get; init; }

    /// <summary>Serialised snapshot, so the caller can build the recovery manifest.</summary>
    public string? SnapshotJson { get; init; }

    public static ElevatedResponse Reject(string requestId, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Rejected = true,
        Message = message
    };

    public static ElevatedResponse Reject(
        string requestId, BrokerFailureReason reason, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Rejected = true,
        Reason = reason,
        Message = message
    };
}

/// <summary>
/// Validates a request before the helper acts on it.
///
/// THE HELPER DOES NOT TRUST THE CALLER
/// ------------------------------------
/// Even though the only caller is KidShell's own UI, every field is checked
/// here as if it were hostile. That is not paranoia about the UI; it is the
/// recognition that an elevated process which trusts its input has made its
/// caller's bugs into privilege escalations.
///
/// Validation is allow-list shaped throughout: a SID must match the SID
/// grammar, a user name must match the Windows rules, a path must be fully
/// qualified and exist. Nothing is sanitised and then used - it is either
/// acceptable or rejected.
/// </summary>
public static class ElevatedRequestValidator
{
    /// <summary>Characters Windows forbids in a local account name.</summary>
    private const string InvalidUserNameCharacters = "\"/\\[]:;|=,+*?<>@";

    /// <summary>Windows caps a local account name at 20 characters.</summary>
    private const int MaxUserNameLength = 20;

    /// <summary>A generated policy larger than this is not something KidShell produced.</summary>
    private const int MaxArtifactBytes = 1024 * 1024;

    public static string? Validate(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion != BrokerEndpoint.ProtocolVersion)
        {
            // First, and without looking at anything else. A message in an
            // unknown protocol has no fields this build can reason about.
            return "Begäran använder ett protokoll som inte stöds.";
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return "Begäran saknar id.";
        }

        if (request.RequestId.Length > MaxRequestIdLength || request.RequestId.Any(char.IsControl))
        {
            // It reaches the audit log. A caller does not get to write
            // arbitrary length or control characters into it.
            return "Begäran har ett ogiltigt id.";
        }

        if (!Enum.IsDefined(request.Kind))
        {
            // An out-of-range enum value means the caller is not the KidShell
            // this helper shipped with.
            return "Okänd åtgärd.";
        }

        return request.Kind switch
        {
            ElevatedOperationKind.Probe => null,

            ElevatedOperationKind.CreateChildAccount =>
                ValidateUserName(request.Username),

            ElevatedOperationKind.DemoteChildAccount =>
                ValidateSid(request.Sid),

            ElevatedOperationKind.ConfigureAutostart =>
                ValidateSid(request.Sid) ?? ValidateAumid(request.Aumid),

            ElevatedOperationKind.DeployAppLockerPolicy =>
                ValidateArtifact(request.PolicyXml, "AppLocker-policyn"),

            ElevatedOperationKind.ConfigureApplicationIdentityService => null,

            ElevatedOperationKind.ConfigureAssignedAccess =>
                ValidateSid(request.Sid) ??
                ValidateArtifact(request.ConfigurationXml, "konfigurationen för begränsat läge"),

            ElevatedOperationKind.DeployBrowserPolicy =>
                ValidateArtifact(request.BrowserPolicyJson, "webbläsarpolicyn"),

            ElevatedOperationKind.InstallWatchdogService =>
                ValidateExecutablePath(request.ExecutablePath),

            ElevatedOperationKind.SaveParentPolicy or
            ElevatedOperationKind.SaveScreenTimeState or
            ElevatedOperationKind.SavePinThrottleState or
            ElevatedOperationKind.MarkProvisioned or
            ElevatedOperationKind.StageParentPolicy =>
                ValidateProtectedPayload(request),

            ElevatedOperationKind.CommitStagedParentPolicy =>
                ValidateDigest(request.ExpectedDigest),

            ElevatedOperationKind.VerifyParentPin =>
                ValidatePinAttempt(request.ParentPinAttempt),

            ElevatedOperationKind.GrantScreenTime =>
                ValidateGrant(request),

            ElevatedOperationKind.ResetScreenTimeToday => null,

            ElevatedOperationKind.InstallSecurityHostService =>
                ValidateExecutablePath(request.ExecutablePath),

            _ => "Okänd åtgärd."
        };
    }

    /// <summary>A request id is a correlation value, not a free-text field.</summary>
    private const int MaxRequestIdLength = 64;

    /// <summary>
    /// A PIN attempt, bounded before it is hashed.
    ///
    /// The length cap is the point: PBKDF2 at 210,000 iterations over a
    /// megabyte supplied by a caller is a way to make a LocalSystem service
    /// burn a core on request. The real PIN policy is checked by the
    /// comparison, not here - this only decides whether the comparison is
    /// worth doing.
    /// </summary>
    private static string? ValidatePinAttempt(string? pin)
    {
        if (string.IsNullOrEmpty(pin))
        {
            return "Koden saknas.";
        }

        if (pin.Length > MaxPinAttemptLength)
        {
            return "Koden är för lång.";
        }

        return pin.All(char.IsAsciiDigit) ? null : "Koden får bara innehålla siffror.";
    }

    /// <summary>
    /// Generous next to the six digits a PIN actually is.
    ///
    /// Deliberately not the PIN policy's own length. The privileged side's
    /// job here is to bound the work, not to re-decide what a valid PIN
    /// looks like - that belongs in one place, and a second copy of it here
    /// would be a second place to change it.
    /// </summary>
    private const int MaxPinAttemptLength = 64;

    private static string? ValidateDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return "Begäran saknar kontrollsumma.";
        }

        // Lowercase hexadecimal SHA-256, exactly. It is compared, never
        // parsed into anything, and a fixed shape keeps it that way.
        return digest.Length == 64 && digest.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'))
            ? null
            : "Kontrollsumman har fel format.";
    }

    private static string? ValidateGrant(ElevatedRequest request)
    {
        if (request.GrantRestOfDay)
        {
            return null;
        }

        // A grant is minutes a parent chose from a small set of buttons. An
        // unbounded one would make "extra time" a way to disable the limit
        // permanently while looking like an ordinary grant.
        return request.GrantMinutes is > 0 and <= MaxGrantMinutes
            ? null
            : "Antalet extraminuter är utanför det tillåtna intervallet.";
    }

    /// <summary>Four hours. More than that is "the rest of today", which is its own flag.</summary>
    public const int MaxGrantMinutes = 240;

    /// <summary>
    /// A protected-state write, checked before the helper looks at its meaning.
    ///
    /// The helper does not trust its caller even though the only intended one
    /// is KidShell's own UI, so this runs on the privileged side regardless of
    /// what the unprivileged side already checked.
    ///
    /// Notice what is NOT here: there is no path to validate, because the
    /// request carries none. Rejecting traversal is not enough when the
    /// contract lets a caller name a destination at all - the contract does
    /// not.
    /// </summary>
    public static string? ValidateProtectedPayload(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtectedPayload is null)
        {
            return "Begäran saknar innehåll.";
        }

        if (request.ProtectedPayload.Length > MaxProtectedPayloadCharacters)
        {
            return "Innehållet är för stort.";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(request.ProtectedPayload) > MaxProtectedPayloadBytes)
        {
            return "Innehållet är för stort.";
        }

        var trimmed = request.ProtectedPayload.TrimStart();

        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return "Innehållet är inte ett JSON-objekt.";
        }

        return null;
    }

    /// <summary>
    /// 256 KiB, far more than any of these documents needs and small enough
    /// that a caller cannot fill a system volume by asking politely and often.
    /// </summary>
    private const int MaxProtectedPayloadBytes = 256 * 1024;

    /// <summary>
    /// Checked before the byte count, so a hostile caller cannot make the
    /// helper walk a gigabyte of characters just to measure it.
    /// </summary>
    private const int MaxProtectedPayloadCharacters = MaxProtectedPayloadBytes;

    public static string? ValidateUserName(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "Kontonamnet saknas.";
        }

        if (username.Length > MaxUserNameLength)
        {
            return $"Kontonamnet får vara högst {MaxUserNameLength} tecken.";
        }

        if (username.EndsWith('.'))
        {
            return "Kontonamnet får inte sluta med punkt.";
        }

        if (username.Any(c => InvalidUserNameCharacters.Contains(c) || char.IsControl(c)))
        {
            return "Kontonamnet innehåller tecken som Windows inte tillåter.";
        }

        // A name that is only dots and spaces is accepted by the checks above
        // and rejected by Windows, so it is rejected here with a message that
        // explains it.
        if (username.Trim(' ', '.').Length == 0)
        {
            return "Kontonamnet måste innehålla tecken.";
        }

        return null;
    }

    public static string? ValidateSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return "Kontots SID saknas.";
        }

        // S-1-5-21-... The grammar is fixed, so it is matched rather than
        // trusted: this value ends up naming a registry hive.
        if (!sid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return "SID har fel format.";
        }

        var parts = sid.Split('-');

        if (parts.Length < 3 || parts.Length > 15)
        {
            return "SID har fel format.";
        }

        // Everything after "S" must be a number. This is what stops a SID
        // field carrying "..\..\SOFTWARE" into a registry path.
        if (parts.Skip(1).Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
        {
            return "SID har fel format.";
        }

        return null;
    }

    public static string? ValidateAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return "Programmets app-id (AUMID) saknas.";
        }

        // PackageFamilyName!ApplicationId
        if (!aumid.Contains('!', StringComparison.Ordinal))
        {
            return "App-id (AUMID) har fel format.";
        }

        if (aumid.Length > 256 || aumid.Any(char.IsControl))
        {
            return "App-id (AUMID) har fel format.";
        }

        // The AUMID is written into a Run value that Windows executes. A quote
        // or an ampersand there would change what the command means.
        if (aumid.Any(c => c is '"' or '\'' or '&' or '|' or '<' or '>' or '^' or '%'))
        {
            return "App-id (AUMID) innehåller otillåtna tecken.";
        }

        return null;
    }

    public static string? ValidateExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Sökvägen saknas.";
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return "Sökvägen måste vara fullständig.";
        }

        if (path.Any(char.IsControl) || path.Contains("..", StringComparison.Ordinal))
        {
            return "Sökvägen är inte tillåten.";
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "Sökvägen måste peka på ett program.";
        }

        return null;
    }

    private static string? ValidateArtifact(string? content, string what)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"Innehållet i {what} saknas.";
        }

        if (content.Length > MaxArtifactBytes)
        {
            return $"Innehållet i {what} är orimligt stort.";
        }

        return null;
    }
}

/// <summary>
/// How requests and responses are serialised between the UI and the helper.
///
/// One line of JSON per message, over stdin/stdout of a child process. No
/// named pipe with an ACL to get wrong, no socket for anything else on the
/// machine to connect to, and a channel that closes when the helper exits.
/// </summary>
public static class ElevatedProtocol
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    public static string Serialize(ElevatedRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static string Serialize(ElevatedResponse response) =>
        JsonSerializer.Serialize(response, Options);

    public static ElevatedRequest? DeserializeRequest(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ElevatedRequest>(line, Options);
        }
        catch (JsonException)
        {
            // Malformed input to an elevated process is rejected, never
            // guessed at.
            return null;
        }
    }

    public static ElevatedResponse? DeserializeResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ElevatedResponse>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
