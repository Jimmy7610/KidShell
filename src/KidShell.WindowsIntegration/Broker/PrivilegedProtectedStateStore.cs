using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;

namespace KidShell.WindowsIntegration.Broker;

/// <summary>
/// Writes a protected document. The privileged half of the boundary.
///
/// Only the elevated helper holds one of these. The unprivileged UI asks for a
/// document to be saved and never sees a path, which is the whole shape of
/// OPSV retest 2 finding 01.
/// </summary>
public interface IPrivilegedProtectedStateStore
{
    ProtectedWriteResult Write(ProtectedDocument document, string json);
}

/// <summary>
/// The real one: a fixed directory under %ProgramData%, one file per document.
///
/// WHAT MAKES THIS SAFE TO RUN AS AN ADMINISTRATOR
/// ----------------------------------------------
/// The caller contributes exactly one thing - the contents - and it is
/// validated before it is written. Everything else is decided here:
///
///   * the directory is a compile-time constant derived from
///     ProtectedStorePlan, not a parameter;
///   * the file name comes from a total switch over a closed enum, not from a
///     string;
///   * there is no member that takes a path, so there is no traversal to
///     reject. Rejecting "..\" would be defence against a contract that
///     should not exist, and this one does not have it.
///
/// A write is temp-then-replace, so an interruption leaves the old document
/// rather than half of the new one.
/// </summary>
public sealed class PrivilegedProtectedStateStore : IPrivilegedProtectedStateStore
{
    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public PrivilegedProtectedStateStore(IKidShellLogger logger)
        : this(ProtectedStorePlan.For(
                   Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).Directory,
               logger)
    {
    }

    internal PrivilegedProtectedStateStore(string directory, IKidShellLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public ProtectedWriteResult Write(ProtectedDocument document, string json)
    {
        // Validated here as well as on the calling side. The calling side's
        // check catches a bug early; this one is the check that matters,
        // because this side does not trust that side.
        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            _logger.Warning("Storage", $"Refused a protected write of {document}: {problem}");
            return ProtectedWriteResult.Reject(problem);
        }

        if (!Enum.IsDefined(document))
        {
            // An out-of-range enum means the caller is not the KidShell this
            // helper shipped with.
            _logger.Warning("Storage", "Refused a protected write for an unknown document.");
            return ProtectedWriteResult.Reject("Okänt dokument.");
        }

        try
        {
            Directory.CreateDirectory(_directory);

            var path = Path.Combine(_directory, ProtectedDocumentNames.FileNameOf(document));
            var temp = path + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);

            // Security-relevant, so it is audited - and deliberately without
            // the contents, which for the policy document include the PIN
            // hash and for the throttle include a cooldown a log reader has no
            // business correlating.
            _logger.Info("Storage", $"Protected document {document} written ({json.Length} characters).");

            return ProtectedWriteResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.Error("Storage", $"Protected write of {document} failed.", ex);
            return ProtectedWriteResult.Fail(ex.GetType().Name);
        }
    }
}
