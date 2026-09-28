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
public sealed class FileSystemProtectedPolicyStore : IProtectedPolicyStore
{
    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public FileSystemProtectedPolicyStore(string directory, IKidShellLogger logger)
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

    public string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var path = System.IO.Path.Combine(_directory, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", $"Protected document '{name}' could not be read.", ex);
            return null;
        }
    }

    public bool Write(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var path = System.IO.Path.Combine(_directory, name);
            var temp = path + ".tmp";

            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);

            return true;
        }
        catch (Exception ex)
        {
            // Expected without elevation, and not an error worth throwing
            // over: writing policy is the parent's privilege, and KidShell
            // normally runs as the child.
            _logger.Warning("Storage",
                $"Protected document '{name}' could not be written; this needs the parent's elevation.", ex);

            return false;
        }
    }
}

/// <summary>
/// A stand-in for development, in the signed-in user's own profile.
///
/// NOT a protected store, and it says so on every probe. The point is that a
/// developer can run the whole product without provisioning a machine, while
/// nothing anywhere can mistake this for the real boundary -
/// <see cref="ProtectedStoreGate"/> is what refuses it in a production build.
/// </summary>
public sealed class DevelopmentProtectedPolicyStore : IProtectedPolicyStore
{
    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public DevelopmentProtectedPolicyStore(string directory, IKidShellLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Directory => _directory;

    public ProtectedStoreState Probe() => new(
        ProtectedStoreStatus.DevelopmentOnly,
        $"'{_directory}' is in the signed-in user's own profile and protects nothing.");

    public string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var path = System.IO.Path.Combine(_directory, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", $"Development policy document '{name}' could not be read.", ex);
            return null;
        }
    }

    public bool Write(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var path = System.IO.Path.Combine(_directory, name);
            var temp = path + ".tmp";

            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("Storage", $"Development policy document '{name}' could not be written.", ex);
            return false;
        }
    }
}

/// <summary>
/// An in-memory protected store, for tests.
///
/// Its probe result is settable, because the interesting cases are all about
/// what happens when the store is NOT ready.
/// </summary>
public sealed class InMemoryProtectedPolicyStore : IProtectedPolicyStore
{
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryProtectedPolicyStore(ProtectedStoreStatus status = ProtectedStoreStatus.Ready) =>
        State = new ProtectedStoreState(status, "(in memory)");

    public ProtectedStoreState State { get; set; }

    /// <summary>Makes every write fail, the way a missing elevation does.</summary>
    public bool RefuseWrites { get; set; }

    public int WriteCount { get; private set; }

    public ProtectedStoreState Probe() => State;

    public string? Read(string name) => _documents.GetValueOrDefault(name);

    public bool Write(string name, string content)
    {
        WriteCount++;

        if (RefuseWrites)
        {
            return false;
        }

        _documents[name] = content;
        return true;
    }

    /// <summary>Replaces a document with something unparseable.</summary>
    public void Corrupt(string name) => _documents[name] = "{ not json";
}
