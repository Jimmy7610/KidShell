using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Storage;

/// <summary>
/// The real protected store: a directory under %ProgramData% that an elevated
/// provisioning step has permissioned so the child can read it and not write
/// it.
///
/// THIS TYPE NEVER PROVISIONS ANYTHING
/// -----------------------------------
/// It does not create the directory, does not set permissions, and does not
/// take ownership. If the store is not there, the honest answer is
/// NotProvisioned - creating it here would produce a directory owned by
/// whoever ran KidShell, which on a locked-down machine is the child, and that
/// is a store that looks like protection and is not.
///
/// Applying the ACLs belongs to the elevated security operation described by
/// <see cref="ProtectedStorePlan"/>, on a dedicated device.
/// </summary>
public sealed class FileSystemProtectedStateReader : IProtectedStateReader
{
    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public FileSystemProtectedStateReader(string directory, IKidShellLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Directory => _directory;

    public ProtectedStoreState Probe()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return new ProtectedStoreState(
                    ProtectedStoreStatus.NotProvisioned,
                    $"'{_directory}' does not exist. An elevated provisioning step creates it.");
            }

            // The question that matters is not "can I read it" but "can the
            // child WRITE it". A store the child can write is worse than no
            // store: it carries the reassurance without the boundary.
            if (CanWrite())
            {
                return new ProtectedStoreState(
                    ProtectedStoreStatus.PermissionsWrong,
                    $"'{_directory}' is writable by the account KidShell is running as.");
            }

            return new ProtectedStoreState(ProtectedStoreStatus.Ready, _directory);
        }
        catch (Exception ex)
        {
            return new ProtectedStoreState(
                ProtectedStoreStatus.Unavailable,
                $"'{_directory}' could not be inspected: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Whether this process can create a file here.
    ///
    /// Asked by trying, because the ACL arithmetic that would answer it on
    /// paper is exactly the kind of reasoning that gets security wrong. The
    /// probe file is removed immediately and is inside a directory that only
    /// exists once somebody has deliberately provisioned it.
    /// </summary>
    private bool CanWrite()
    {
        var probe = System.IO.Path.Combine(_directory, $".kidshell-write-probe-{Guid.NewGuid():n}");

        try
        {
            using (var stream = File.Create(probe))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception)
            {
                // Best effort. A leftover probe file is harmless and will be
                // overwritten by the next one.
            }
        }
    }

    public string? Read(ProtectedDocument document)
    {
        try
        {
            // The name comes from the enum, never from a caller.
            var path = System.IO.Path.Combine(_directory, ProtectedDocumentNames.FileNameOf(document));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", $"Protected document {document} could not be read.", ex);
            return null;
        }
    }

    // NO Write. That was OPSV retest 2 finding 01: this store is trustworthy
    // only when the account KidShell runs as CANNOT write here, and the same
    // process was then expected to write to it. Writing goes through
    // IProtectedStateWriter and the privileged helper.
}

/// <summary>
/// A stand-in for development, in the signed-in user's own profile.
///
/// NOT a protected store, and it says so on every probe. The point is that a
/// developer can run the whole product without provisioning a machine, while
/// nothing anywhere can mistake this for the real boundary -
/// <see cref="ProtectedStoreGate"/> is what refuses it in a production build.
/// </summary>
public sealed class DevelopmentProtectedStateReader : IProtectedStateReader
{
    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public DevelopmentProtectedStateReader(string directory, IKidShellLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Directory => _directory;

    public ProtectedStoreState Probe() => new(
        ProtectedStoreStatus.DevelopmentOnly,
        $"'{_directory}' is in the signed-in user's own profile and protects nothing.");

    public string? Read(ProtectedDocument document)
    {
        try
        {
            var path = System.IO.Path.Combine(_directory, ProtectedDocumentNames.FileNameOf(document));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", $"Development policy document {document} could not be read.", ex);
            return null;
        }
    }

    // NO Write here either. A development build writes through
    // DirectProtectedStateWriter, which is a writer and says so.
}

/// <summary>
/// An in-memory protected store, for tests.
///
/// Its probe result is settable, because the interesting cases are all about
/// what happens when the store is NOT ready.
/// </summary>
public sealed class InMemoryProtectedStateStore : IProtectedStateReader, IProtectedStateWriter
{
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryProtectedStateStore(ProtectedStoreStatus status = ProtectedStoreStatus.Ready) =>
        State = new ProtectedStoreState(status, "(in memory)");

    public ProtectedStoreState State { get; set; }

    /// <summary>Makes every write fail, the way a missing elevation does.</summary>
    public bool RefuseWrites { get; set; }

    public int WriteCount { get; private set; }

    public ProtectedStoreState Probe() => State;

    public string? Read(ProtectedDocument document) =>
        _documents.GetValueOrDefault(ProtectedDocumentNames.FileNameOf(document));

    public bool IsAvailable { get; set; } = true;

    public ProtectedWriteResult SaveParentPolicy(string json) =>
        Write(ProtectedDocument.ParentPolicy, json);

    /// <summary>What was staged, and never read for enforcement.</summary>
    public string? Staged { get; private set; }

    public ProtectedStageResult StageParentPolicy(string json)
    {
        WriteCount++;

        if (!IsAvailable)
        {
            return ProtectedStageResult.Unavailable("(test) writer unavailable");
        }

        if (RefuseWrites)
        {
            return ProtectedStageResult.Fail("(test) refused");
        }

        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedStageResult.Reject(problem);
        }

        Staged = json;
        return ProtectedStageResult.Staged(ProtectedDocumentNames.DigestOf(json));
    }

    /// <summary>Approves whatever is staged, the way an administrator would.</summary>
    public ProtectedWriteResult ApproveStaged(string digest)
    {
        if (Staged is null || !string.Equals(ProtectedDocumentNames.DigestOf(Staged), digest, StringComparison.Ordinal))
        {
            return ProtectedWriteResult.Reject("(test) nothing staged under that digest");
        }

        var result = Write(ProtectedDocument.ParentPolicy, Staged);

        if (result.Success)
        {
            Staged = null;
        }

        return result;
    }

    public ProtectedWriteResult SaveScreenTimeState(string json) =>
        Write(ProtectedDocument.ScreenTimeState, json);

    public ProtectedWriteResult SavePinThrottleState(string json) =>
        Write(ProtectedDocument.PinThrottleState, json);

    public ProtectedWriteResult MarkProvisioned(string json) =>
        Write(ProtectedDocument.ProvisioningMarker, json);

    private ProtectedWriteResult Write(ProtectedDocument document, string json)
    {
        WriteCount++;

        if (!IsAvailable)
        {
            return ProtectedWriteResult.Unavailable("(test) writer unavailable");
        }

        if (RefuseWrites)
        {
            return ProtectedWriteResult.Fail("(test) refused");
        }

        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedWriteResult.Reject(problem);
        }

        _documents[ProtectedDocumentNames.FileNameOf(document)] = json;
        return ProtectedWriteResult.Ok();
    }

    /// <summary>Seeds a document without going through the writer.</summary>
    public void Seed(ProtectedDocument document, string json) =>
        _documents[ProtectedDocumentNames.FileNameOf(document)] = json;

    /// <summary>Replaces a document with something unparseable.</summary>
    public void Corrupt(ProtectedDocument document) =>
        _documents[ProtectedDocumentNames.FileNameOf(document)] = "{ not json";

    /// <summary>Removes a document, the way a determined child would.</summary>
    public void Delete(ProtectedDocument document) =>
        _documents.Remove(ProtectedDocumentNames.FileNameOf(document));
}

/// <summary>
/// The privileged side of the store, in memory.
///
/// Stands in for the directory under %ProgramData% that only a LocalSystem
/// service can write. Everything the broker decides - the monotonic rules,
/// the authorization matrix, the staging slot - is decided against a store
/// like this one in the tests, because none of those decisions needs a real
/// access control list to be wrong.
/// </summary>
public sealed class InMemoryPrivilegedStore : KidShell.Core.Security.Broker.IPrivilegedProtectedStore
{
    private readonly Dictionary<ProtectedDocument, string> _documents = [];

    /// <summary>Makes every write fail, the way a full disk would.</summary>
    public bool RefuseWrites { get; set; }

    public string? Staged { get; private set; }

    public int WriteCount { get; private set; }

    public string? Read(ProtectedDocument document) => _documents.GetValueOrDefault(document);

    public ProtectedWriteResult Write(ProtectedDocument document, string json)
    {
        WriteCount++;

        if (RefuseWrites)
        {
            return ProtectedWriteResult.Fail("(test) refused");
        }

        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedWriteResult.Reject(problem);
        }

        _documents[document] = json;
        return ProtectedWriteResult.Ok();
    }

    public string? ReadStaged() => Staged;

    public ProtectedWriteResult WriteStaged(string json)
    {
        if (RefuseWrites)
        {
            return ProtectedWriteResult.Fail("(test) refused");
        }

        if (ProtectedPayloadPolicy.Validate(json) is { } problem)
        {
            return ProtectedWriteResult.Reject(problem);
        }

        Staged = json;
        return ProtectedWriteResult.Ok();
    }

    public ProtectedWriteResult ClearStaged()
    {
        Staged = null;
        return ProtectedWriteResult.Ok();
    }

    /// <summary>Puts a document in place without going through a write.</summary>
    public void Seed(ProtectedDocument document, string json) => _documents[document] = json;

    /// <summary>Replaces the staged slot behind the approver's back.</summary>
    public void TamperStaged(string json) => Staged = json;
}
