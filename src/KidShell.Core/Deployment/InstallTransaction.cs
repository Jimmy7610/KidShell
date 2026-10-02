using System.Security.Cryptography;
using System.Text;

namespace KidShell.Core.Deployment;

/// <summary>
/// The file operations an install needs, as an interface.
///
/// Not for tidiness. The thing that must be tested here is what happens when an
/// install fails HALFWAY - after some files are down and before the rest are -
/// and that cannot be tested against a real Program Files without a test that
/// writes to Program Files. So the transaction is written against this, and the
/// tests hand it a fake that can be told to fail on the fourth copy.
/// </summary>
public interface IInstallFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    /// <summary>Lowercase hexadecimal SHA-256 of the file's contents.</summary>
    string Sha256(string path);

    long Length(string path);

    void Copy(string source, string destination, bool overwrite);

    void DeleteFile(string path);

    /// <summary>Every file under a directory, as full paths.</summary>
    IReadOnlyList<string> EnumerateFiles(string directory);

    /// <summary>Removes a directory when it holds no files. Never recursive.</summary>
    void DeleteDirectoryIfEmpty(string path);
}

/// <summary>What one install attempt did.</summary>
/// <param name="Committed">Whether the install completed and was verified.</param>
/// <param name="RolledBack">Whether a failure was undone.</param>
/// <param name="NeedsManualRecovery">
/// Whether the rollback itself failed. The one state that needs a human, and
/// the one the recovery record exists for.
/// </param>
public sealed record InstallOutcome(
    bool Committed,
    bool RolledBack,
    bool NeedsManualRecovery,
    IReadOnlyList<string> Log,
    IReadOnlyDictionary<string, string> WrittenFiles)
{
    public static InstallOutcome Failed(IReadOnlyList<string> log, bool rolledBack, bool manual) =>
        new(false, rolledBack, manual, log, new Dictionary<string, string>());
}

/// <summary>
/// Lays a bundle down, and undoes it if anything goes wrong.
///
/// THE ORDER, AND WHY IT IS THIS ORDER
/// -----------------------------------
///   preflight    nothing has been touched yet, so everything that can be
///                refused is refused here
///   snapshot     what is currently installed, before it is disturbed
///   stage        copy into a staging directory, still touching nothing real
///   verify       every staged file's digest against the manifest
///   install      move into place, recording each file as it lands
///   verify       the installed files, again, where they now are
///   commit       the receipt
///
/// The second verification is not redundant. The first proves the bundle was
/// intact; the second proves what is on the disk now is what was verified.
/// Between them is the only window in which a file can be substituted, and the
/// cost of closing it is one more hash of files that are already in the cache.
///
/// NOTHING HERE ENABLES ANY LOCKDOWN. It copies files. It does not create an
/// account, register a service, write an access list, touch AppLocker,
/// configure Assigned Access or change the shell. Installing the binaries and
/// enabling the security are different operations on purpose: a person must be
/// able to do the first without the second.
/// </summary>
public sealed class InstallTransaction
{
    private readonly IInstallFileSystem _files;
    private readonly List<string> _log = [];

    public InstallTransaction(IInstallFileSystem files) => _files = files;

    /// <summary>
    /// Runs the install.
    ///
    /// <paramref name="bundleRoot"/> is where the verified bundle sits.
    /// <paramref name="installRoot"/> is the fixed destination, from
    /// <see cref="InstallationLayout"/> - never from the manifest.
    /// </summary>
    public InstallOutcome Execute(
        ReleaseManifest manifest,
        string bundleRoot,
        string installRoot,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var staged = new List<(string Staged, string Destination, string RelativePath, string Digest)>();

        // --------------------------------------------------- 1. preflight

        foreach (var component in manifest.Components)
        {
            if (component.Component == KidShellComponent.App)
            {
                // An MSIX is registered, not copied. A file copy of a packaged
                // app into Program Files produces something that looks
                // installed and will not start.
                Note($"skipping {component.Component}: it is a package, not files");
                continue;
            }

            var destinationDirectory = Combine(installRoot, InstallationLayout.FolderOf(component.Component));

            foreach (var file in component.Files)
            {
                if (!InstallationLayout.IsSafeRelativePath(file.Path))
                {
                    return Fail($"refused an unsafe path from the manifest: '{file.Path}'", false, false);
                }

                var destination = Combine(destinationDirectory, file.Path);

                // Checked after combining as well as before, because a path
                // that reads safely can still resolve outside the root.
                if (!InstallationLayout.IsInsideRoot(installRoot, destination))
                {
                    return Fail($"refused a destination outside the install root: '{destination}'", false, false);
                }

                var source = Combine(Combine(bundleRoot, component.BundlePath), file.Path);

                if (!_files.FileExists(source))
                {
                    return Fail($"the bundle is missing '{component.BundlePath}\\{file.Path}'", false, false);
                }

                staged.Add((
                    Combine(Combine(stagingRoot, InstallationLayout.FolderOf(component.Component)), file.Path),
                    destination,
                    $"{InstallationLayout.FolderOf(component.Component)}\\{file.Path}",
                    file.Sha256));
            }
        }

        if (staged.Count == 0)
        {
            return Fail("the manifest named no files to install", false, false);
        }

        Note($"preflight passed: {staged.Count} file(s) to install");

        // ------------------------------------------- 2. stage, 3. verify

        try
        {
            foreach (var component in manifest.Components.Where(c => c.Component != KidShellComponent.App))
            {
                foreach (var file in component.Files)
                {
                    var source = Combine(Combine(bundleRoot, component.BundlePath), file.Path);
                    var target = Combine(
                        Combine(stagingRoot, InstallationLayout.FolderOf(component.Component)), file.Path);

                    _files.CreateDirectory(DirectoryOf(target));
                    _files.Copy(source, target, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing real has been touched, so there is nothing to undo.
            return Fail($"staging failed: {ex.GetType().Name}", false, false);
        }

        foreach (var (stagedPath, _, relative, digest) in staged)
        {
            if (!_files.FileExists(stagedPath))
            {
                return Fail($"'{relative}' did not reach the staging directory", false, false);
            }

            var actual = _files.Sha256(stagedPath);

            if (!string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    $"'{relative}' does not match the manifest (expected {Short(digest)}, got {Short(actual)})",
                    false, false);
            }
        }

        Note("every staged file matches the manifest");

        // ------------------------------------------------------ 4. install

        var landed = new List<string>();

        try
        {
            foreach (var (stagedPath, destination, relative, digest) in staged)
            {
                _files.CreateDirectory(DirectoryOf(destination));
                _files.Copy(stagedPath, destination, overwrite: true);

                landed.Add(destination);
                written[relative] = digest;
            }
        }
        catch (Exception ex)
        {
            Note($"install failed after {landed.Count} file(s): {ex.GetType().Name}");

            var rolledBack = Rollback(landed);

            return InstallOutcome.Failed(_log, rolledBack, !rolledBack);
        }

        // --------------------------------------------- 5. verify in place

        foreach (var (_, destination, relative, digest) in staged)
        {
            if (!_files.FileExists(destination))
            {
                Note($"'{relative}' is not where it was installed");

                var rolledBack = Rollback(landed);
                return InstallOutcome.Failed(_log, rolledBack, !rolledBack);
            }

            var actual = _files.Sha256(destination);

            if (!string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase))
            {
                // Between staging and here is the only window a substitution
                // fits into. Narrow, and not zero.
                Note($"'{relative}' changed between staging and installation");

                var rolledBack = Rollback(landed);
                return InstallOutcome.Failed(_log, rolledBack, !rolledBack);
            }
        }

        Note($"installed and verified {written.Count} file(s)");

        return new InstallOutcome(true, false, false, _log, written);
    }

    /// <summary>
    /// Removes what this attempt had already written.
    ///
    /// Only what it wrote. A rollback that removed the install root would take
    /// away a previous working installation that this attempt was replacing,
    /// which turns a failed upgrade into a machine with nothing on it.
    /// </summary>
    private bool Rollback(IReadOnlyList<string> landed)
    {
        var ok = true;

        foreach (var path in landed.Reverse())
        {
            try
            {
                if (_files.FileExists(path))
                {
                    _files.DeleteFile(path);
                }
            }
            catch (Exception ex)
            {
                Note($"rollback could not remove '{path}': {ex.GetType().Name}");
                ok = false;
            }
        }

        foreach (var directory in landed.Select(DirectoryOf).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _files.DeleteDirectoryIfEmpty(directory);
            }
            catch
            {
                // A directory that would not go is not a failed rollback: the
                // files are what mattered, and an empty folder is not a
                // half-installed product.
            }
        }

        Note(ok ? "rolled back cleanly" : "ROLLBACK INCOMPLETE - follow the recovery record");

        return ok;
    }

    private InstallOutcome Fail(string why, bool rolledBack, bool manual)
    {
        Note(why);
        return InstallOutcome.Failed(_log, rolledBack, manual);
    }

    private void Note(string message) => _log.Add(message);

    private static string Short(string digest) => digest.Length >= 12 ? digest[..12] : digest;

    private static string Combine(string left, string right) =>
        (left ?? string.Empty).TrimEnd('\\', '/') + "\\" + (right ?? string.Empty).TrimStart('\\', '/');

    private static string DirectoryOf(string path)
    {
        var index = path.LastIndexOf('\\');
        return index > 0 ? path[..index] : path;
    }

    /// <summary>Lowercase hexadecimal SHA-256, the one definition everything here uses.</summary>
    public static string Digest(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Digest(string content) => Digest(Encoding.UTF8.GetBytes(content));
}

/// <summary>Whether what is on disk is what the manifest said it would be.</summary>
public enum InstallVerificationStatus
{
    /// <summary>Something could not be checked. Never a pass.</summary>
    Incomplete = 0,

    Pass = 1,
    Fail = 2
}

/// <summary>One thing the verifier looked at.</summary>
public sealed record InstallCheck(InstallVerificationStatus Status, string Detail);

/// <summary>
/// The read-only verifier's rules.
///
/// THE RULE THAT MATTERS: a skipped check is not a pass. If signature
/// verification was expected and did not happen, the answer is INCOMPLETE, not
/// PASS - because the whole point of signing is that somebody later relies on
/// it having been checked.
/// </summary>
public static class InstallVerification
{
    public static InstallVerificationStatus Overall(IReadOnlyList<InstallCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        if (checks.Count == 0)
        {
            return InstallVerificationStatus.Incomplete;
        }

        if (checks.Any(c => c.Status == InstallVerificationStatus.Fail))
        {
            return InstallVerificationStatus.Fail;
        }

        return checks.Any(c => c.Status == InstallVerificationStatus.Incomplete)
            ? InstallVerificationStatus.Incomplete
            : InstallVerificationStatus.Pass;
    }

    /// <summary>
    /// Compares an installed tree against the receipt that describes it.
    ///
    /// <paramref name="signatureChecked"/> is whether signatures were actually
    /// verified. When the receipt says the build was signed and this is false,
    /// the result is INCOMPLETE however many files matched.
    /// </summary>
    public static IReadOnlyList<InstallCheck> Check(
        InstallReceipt? receipt,
        IInstallFileSystem files,
        string installRoot,
        bool signatureChecked)
    {
        ArgumentNullException.ThrowIfNull(files);

        var checks = new List<InstallCheck>();

        if (receipt is null)
        {
            checks.Add(new InstallCheck(InstallVerificationStatus.Incomplete,
                "There is no install receipt, so nothing can be compared against what should be here."));

            return checks;
        }

        if (!files.DirectoryExists(installRoot))
        {
            checks.Add(new InstallCheck(InstallVerificationStatus.Fail,
                $"The install root '{installRoot}' does not exist, and a receipt says it should."));

            return checks;
        }

        foreach (var (relative, expected) in receipt.Files)
        {
            var path = installRoot.TrimEnd('\\') + "\\" + relative;

            if (!files.FileExists(path))
            {
                checks.Add(new InstallCheck(InstallVerificationStatus.Fail, $"'{relative}' is missing."));
                continue;
            }

            var actual = files.Sha256(path);

            checks.Add(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? new InstallCheck(InstallVerificationStatus.Pass, $"'{relative}' matches.")
                : new InstallCheck(InstallVerificationStatus.Fail, $"'{relative}' has changed since it was installed."));
        }

        // Files under the install root that the receipt does not mention.
        // Reported, not deleted and not failed: something put them there, and
        // on a lab device that something is often the operator.
        var unexpected = files.EnumerateFiles(installRoot)
            .Select(p => p.Substring(Math.Min(p.Length, installRoot.TrimEnd('\\').Length + 1)))
            .Where(relative => relative.Length > 0 && !receipt.Files.ContainsKey(relative))
            .ToList();

        if (unexpected.Count > 0)
        {
            checks.Add(new InstallCheck(InstallVerificationStatus.Incomplete,
                $"{unexpected.Count} file(s) under the install root are not in the receipt: " +
                string.Join(", ", unexpected.Take(5)) + (unexpected.Count > 5 ? ", ..." : "")));
        }

        if (receipt.Signed && !signatureChecked)
        {
            checks.Add(new InstallCheck(InstallVerificationStatus.Incomplete,
                "The receipt says this build was signed, and no signature was verified. " +
                "That is not a pass: the point of signing is that somebody later relies on the check."));
        }
        else if (!receipt.Signed)
        {
            checks.Add(new InstallCheck(InstallVerificationStatus.Pass,
                $"This is an UNSIGNED {receipt.Channel} installation. Nothing about it may be presented as a release."));
        }

        if (string.IsNullOrWhiteSpace(receipt.GitSha))
        {
            checks.Add(new InstallCheck(InstallVerificationStatus.Fail,
                "The receipt records no commit, so what is installed cannot be established."));
        }

        return checks;
    }
}
