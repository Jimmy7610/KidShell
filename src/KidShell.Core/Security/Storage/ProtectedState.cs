using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Storage;

/// <summary>
/// The documents that live behind the trust boundary.
///
/// A CLOSED ENUM, NOT A PATH
/// -------------------------
/// OPSV RETEST 2, FINDING 01. The child process must never choose where a
/// write lands. If it could, the privileged writer would be a
/// general-purpose "write anything anywhere as an administrator" service, and
/// the boundary would exist only in the documentation.
///
/// So the caller names a DOCUMENT, and the privileged side owns the mapping
/// from document to location. There are three, they are fixed at compile time,
/// and adding a fourth is a reviewable code change on both sides.
/// </summary>
public enum ProtectedDocument
{
    /// <summary>The parent's decisions: apps, web, screen-time settings, PIN material.</summary>
    ParentPolicy = 0,

    /// <summary>Today's usage counter and its session journal.</summary>
    ScreenTimeState = 1,

    /// <summary>Failed PIN attempts and the cooldown they earned.</summary>
    PinThrottleState = 2,

    /// <summary>
    /// The marker written once when a machine is provisioned.
    ///
    /// It is what distinguishes "this device has never been set up" from "the
    /// policy that was here is gone", which are the same absence and very
    /// different facts. See <see cref="ProtectedPolicyTrust"/>.
    /// </summary>
    ProvisioningMarker = 3
}

/// <summary>Why a protected write did not happen.</summary>
public enum ProtectedWriteStatus
{
    Written = 0,

    /// <summary>No privileged writer is available on this machine.</summary>
    WriterUnavailable = 1,

    /// <summary>The writer refused the request. The payload is at fault, not the machine.</summary>
    Rejected = 2,

    /// <summary>The writer accepted it and the write itself failed.</summary>
    Failed = 3
}

/// <summary>The outcome of one protected write.</summary>
public sealed record ProtectedWriteResult(ProtectedWriteStatus Status, string Detail = "")
{
    public bool Success => Status == ProtectedWriteStatus.Written;

    public static ProtectedWriteResult Ok() => new(ProtectedWriteStatus.Written);

    public static ProtectedWriteResult Unavailable(string detail) =>
        new(ProtectedWriteStatus.WriterUnavailable, detail);

    public static ProtectedWriteResult Reject(string detail) =>
        new(ProtectedWriteStatus.Rejected, detail);

    public static ProtectedWriteResult Fail(string detail) =>
        new(ProtectedWriteStatus.Failed, detail);
}

/// <summary>
/// Reads the protected store. Separate from writing on purpose.
///
/// The child process reads its own policy - it has to, in order to enforce it -
/// and never writes. Those were one interface, and that was the architectural
/// contradiction OPSV found: the store was only trustworthy when the child
/// could NOT write to it, and the same child process was then expected to
/// write to it.
/// </summary>
public interface IProtectedStateReader
{
    /// <summary>Whether this store can be trusted, and why not when it cannot.</summary>
    ProtectedStoreState Probe();

    /// <summary>Reads a document, or null when it is not there.</summary>
    string? Read(ProtectedDocument document);
}

/// <summary>
/// Writes to the protected store, through whatever holds the privilege.
///
/// Every operation is typed and named. There is deliberately no
/// <c>Write(path, bytes)</c>, no <c>WriteJson(name, content)</c> and no
/// destination parameter of any kind: the caller says WHAT it is saving and
/// the implementation decides WHERE, because the caller is the unprivileged
/// side and the boundary is the whole point.
/// </summary>
public interface IProtectedStateWriter
{
    /// <summary>Whether a write could be attempted at all right now.</summary>
    bool IsAvailable { get; }

    ProtectedWriteResult SaveParentPolicy(string json);

    ProtectedWriteResult SaveScreenTimeState(string json);

    ProtectedWriteResult SavePinThrottleState(string json);

    /// <summary>
    /// Records that this machine has been provisioned.
    ///
    /// Written once, by the provisioning step. Afterwards, an absent policy
    /// means a missing policy rather than a new machine.
    /// </summary>
    ProtectedWriteResult MarkProvisioned(string json);
}

/// <summary>
/// What a protected payload may be before the privileged side even looks at
/// its meaning.
///
/// Checked on both sides. The unprivileged side checks so a bug is caught
/// early; the privileged side checks because it does not trust the
/// unprivileged side, which is the only reason that matters.
/// </summary>
public static class ProtectedPayloadPolicy
{
    /// <summary>
    /// The largest document the protected store will accept.
    ///
    /// 256 KiB is far more than any of these documents needs - the largest is
    /// a policy with forty apps and twenty websites - and small enough that a
    /// caller cannot fill a system volume by asking politely and often.
    /// </summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>Returns null when the payload is acceptable, or why it is not.</summary>
    public static string? Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "Innehållet saknas.";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
        {
            return $"Innehållet är större än {MaxPayloadBytes} byte.";
        }

        // A document, not an array and not a bare value. Cheap, and it means
        // the privileged side never hands something shapeless to a
        // deserialiser.
        var trimmed = json.TrimStart();

        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return "Innehållet är inte ett JSON-objekt.";
        }

        return null;
    }
}

/// <summary>
/// A writer for development builds and for tests: writes straight to a
/// directory, with no privilege involved.
///
/// Labelled rather than hidden. It is only ever reachable when the runtime
/// environment is a development build, and <see cref="ProtectedStoreGate"/> is
/// what enforces that.
/// </summary>
public sealed class DirectProtectedStateWriter : IProtectedStateWriter
{
    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public DirectProtectedStateWriter(string directory, IKidShellLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public ProtectedWriteResult SaveParentPolicy(string json) =>
        Write(ProtectedDocument.ParentPolicy, json);

    public ProtectedWriteResult SaveScreenTimeState(string json) =>
        Write(ProtectedDocument.ScreenTimeState, json);

    public ProtectedWriteResult SavePinThrottleState(string json) =>
        Write(ProtectedDocument.PinThrottleState, json);

    public ProtectedWriteResult MarkProvisioned(string json) =>
        Write(ProtectedDocument.ProvisioningMarker, json);

    private ProtectedWriteResult Write(ProtectedDocument document, string json)
    {
        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedWriteResult.Reject(problem);
        }

        try
        {
            Directory.CreateDirectory(_directory);

            // The name comes from the enum, never from a caller.
            var path = Path.Combine(_directory, ProtectedDocumentNames.FileNameOf(document));
            var temp = path + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);

            return ProtectedWriteResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", $"Development protected write of {document} failed.", ex);
            return ProtectedWriteResult.Fail(ex.GetType().Name);
        }
    }
}

/// <summary>
/// The file name each document lives under.
///
/// One place, used by the reader and by every writer, so the two sides cannot
/// disagree about where a document is. The mapping is total over the enum, so
/// adding a document without deciding its name does not compile.
/// </summary>
public static class ProtectedDocumentNames
{
    public static string FileNameOf(ProtectedDocument document) => document switch
    {
        ProtectedDocument.ParentPolicy => "parent-policy.json",
        ProtectedDocument.ScreenTimeState => "screen-time-state.json",
        ProtectedDocument.PinThrottleState => "pin-throttle.json",
        ProtectedDocument.ProvisioningMarker => "provisioned.json",
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown protected document.")
    };
}
