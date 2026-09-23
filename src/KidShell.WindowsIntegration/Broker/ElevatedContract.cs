using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidShell.WindowsIntegration.Broker;

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
    InstallWatchdogService = 8
}

/// <summary>
/// One typed request to the elevated helper.
///
/// Every field is a value the helper validates before use. Nothing here is
/// interpreted as a command, a path to execute, or a script.
/// </summary>
public sealed record ElevatedRequest
{
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

    /// <summary>Whether the helper should apply, or only report what it would do.</summary>
    public bool DryRun { get; init; } = true;
}

/// <summary>What the helper did, or refused to do.</summary>
public sealed record ElevatedResponse
{
    public required string RequestId { get; init; }

    public required bool Success { get; init; }

    /// <summary>Parent-facing, Swedish, never an HRESULT.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Technical detail for the log. Never shown to a child.</summary>
    public string? Detail { get; init; }

    /// <summary>Set when the request was rejected before anything ran.</summary>
    public bool Rejected { get; init; }

    /// <summary>Serialised snapshot, so the caller can build the recovery manifest.</summary>
    public string? SnapshotJson { get; init; }

    public static ElevatedResponse Reject(string requestId, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Rejected = true,
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

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return "Begäran saknar id.";
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

            _ => "Okänd åtgärd."
        };
    }

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
