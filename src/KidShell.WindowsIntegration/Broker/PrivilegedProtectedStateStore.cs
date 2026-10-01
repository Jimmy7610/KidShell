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
public sealed class PrivilegedProtectedStateStore : IPrivilegedProtectedStateStore, IPrivilegedProtectedStore
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

    /// <summary>
    /// Reads a document back.
    ///
    /// PRIVILEGED BROKER HARDENING. The service has to know what it already
    /// holds, because every monotonic rule compares a proposal against the
    /// current value. A privileged writer that cannot read can only write
    /// what it is told, which is what made routing the screen-time counter
    /// through LocalSystem achieve nothing: the child would simply have asked
    /// SYSTEM to write a zero.
    /// </summary>
    public string? Read(ProtectedDocument document)
    {
        if (!Enum.IsDefined(document))
        {
            return null;
        }

        return ReadFile(Path.Combine(_directory, ProtectedDocumentNames.FileNameOf(document)));
    }

    /// <summary>
    /// The policy a child's session has proposed.
    ///
    /// Stored next to the authoritative documents and treated as none of
    /// them. Nothing reads this for enforcement - that is the property that
    /// makes it safe for the unprivileged side to fill.
    /// </summary>
    public string? ReadStaged() =>
        ReadFile(Path.Combine(_directory, ProtectedDocumentNames.StagedParentPolicyFileName));

    public ProtectedWriteResult WriteStaged(string json)
    {
        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            _logger.Warning("Storage", $"Refused a staged policy: {problem}");
            return ProtectedWriteResult.Reject(problem);
        }

        return WriteFile(ProtectedDocumentNames.StagedParentPolicyFileName, json, "staged parent policy");
    }

    public ProtectedWriteResult ClearStaged()
    {
        try
        {
            var path = Path.Combine(_directory, ProtectedDocumentNames.StagedParentPolicyFileName);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return ProtectedWriteResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", "The staged parent policy could not be removed.", ex);
            return ProtectedWriteResult.Fail(ex.GetType().Name);
        }
    }

    private string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.Error("Storage", "A protected document could not be read.", ex);
            return null;
        }
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

        return WriteFile(ProtectedDocumentNames.FileNameOf(document), json, document.ToString());
    }

    /// <summary>
    /// The one place bytes reach the disk.
    ///
    /// <paramref name="fileName"/> is always a compile-time constant from
    /// this assembly or a total switch over the document enum. It is never
    /// anything a caller supplied, which is why there is no traversal check:
    /// there is nothing here for a caller to traverse with.
    /// </summary>
    private ProtectedWriteResult WriteFile(string fileName, string json, string what)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var path = Path.Combine(_directory, fileName);
            var temp = path + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);

            // Security-relevant, so it is audited - and deliberately without
            // the contents, which for the policy document include the PIN
            // hash and for the throttle include a cooldown a log reader has no
            // business correlating.
            _logger.Info("Storage", $"Protected document {what} written ({json.Length} characters).");

            return ProtectedWriteResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.Error("Storage", $"Protected write of {what} failed.", ex);
            return ProtectedWriteResult.Fail(ex.GetType().Name);
        }
    }
}
