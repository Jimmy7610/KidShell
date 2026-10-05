using System.Security.Cryptography;
using System.Text.Json;
using KidShell.Core.Deployment;
using KidShell.Core.Security.Validation;

namespace KidShell.DeviceValidation;

/// <summary>
/// The real filesystem, behind the interface the transaction is written
/// against.
///
/// Everything interesting about the install - the staging, the two
/// verifications, the rollback - is in <see cref="InstallTransaction"/> and is
/// tested against a fake. This is the part that cannot be, so it is kept as
/// close to nothing as it can be: eight methods, each one line.
/// </summary>
internal sealed class PhysicalInstallFileSystem : IInstallFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public long Length(string path) => new FileInfo(path).Length;

    public void Copy(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);

    public void DeleteFile(string path) => File.Delete(path);

    public IReadOnlyList<string> EnumerateFiles(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)]
            : [];

    public void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) &&
            !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }
}

/// <summary>
/// The install, uninstall and verify verbs.
///
/// WHY THE INSTALLER'S ENGINE IS HERE AND NOT IN POWERSHELL
/// --------------------------------------------------------
/// The staging, the double verification and above all the ROLLBACK are the
/// parts that have to be right, and the only way to test a rollback is to make
/// an install fail halfway - which cannot be done against a real Program Files
/// by a test that is not allowed to write to Program Files. So the transaction
/// is written against an interface, tested against a fake, and run here.
///
/// A PowerShell reimplementation would be a second rollback with no tests, and
/// it would be the one that ran.
///
/// EVERY MUTATING VERB NEEDS --apply. Without it they describe and change
/// nothing. The script in front of them has already passed the dedicated-device
/// interlock; this is the second gate, not the first.
/// </summary>
internal static class InstallVerbs
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Prints a path from the layout, so a script never computes one itself.
    ///
    /// WHY A VERB FOR A STRING
    /// ----------------------
    /// build-release.ps1 has to know where a component's build output is, and
    /// the first version of this pass had that path written out in PowerShell
    /// as well as in <see cref="InstallationLayout"/> - two definitions, which
    /// is precisely the arrangement that shipped week-old binaries in the first
    /// place. One of them would eventually have been updated alone.
    ///
    /// So the script asks. The tool is built by the same solution build that
    /// precedes packaging, so it is there when the bundler needs it.
    /// </summary>
    internal static int Layout(string? what, string? component, string? configuration, string? platform)
    {
        if (what is null)
        {
            Console.Error.WriteLine("layout needs --what (build-output, folder, install-root, policy, receipt).");
            return 2;
        }

        KidShellComponent parsed = default;

        if (component is not null &&
            !Enum.TryParse(component.Replace("KidShell.", string.Empty), ignoreCase: true, out parsed))
        {
            Console.Error.WriteLine($"Unknown component '{component}'.");
            return 2;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        switch (what)
        {
            case "build-output":
                if (component is null)
                {
                    Console.Error.WriteLine("build-output needs --component.");
                    return 2;
                }

                Console.WriteLine(InstallationLayout.BuildOutputOf(
                    parsed, configuration ?? "Release", platform ?? "x64"));
                return 0;

            case "folder":
                Console.WriteLine(InstallationLayout.FolderOf(parsed));
                return 0;

            case "components":
                foreach (var each in InstallationLayout.FileCopied)
                {
                    Console.WriteLine(InstallationLayout.ProjectOf(each));
                }

                return 0;

            case "install-root":
                Console.WriteLine(InstallationLayout.InstallRoot(programFiles));
                return 0;

            case "policy":
                Console.WriteLine(InstallationLayout.PolicyDirectory(programData));
                return 0;

            case "receipt":
                Console.WriteLine(InstallationLayout.ReceiptFile(programData));
                return 0;

            default:
                Console.Error.WriteLine($"Unknown --what '{what}'.");
                return 2;
        }
    }

    /// <summary>
    /// Prints the built-in groups the toolset reasons about, as label and SID.
    ///
    /// WHY THE SCRIPTS ASK FOR THIS
    /// ---------------------------
    /// Windows localizes its built-in group names. On the Swedish machine used
    /// for physical validation, `Get-LocalGroup -Name 'Administrators'` throws.
    /// So no script may name a group; every one of them resolves by SID, and
    /// this is where the SIDs come from - one table, in C#, with tests, rather
    /// than a copy in each script that needs one.
    /// </summary>
    internal static int SecurityGroups(string? set)
    {
        var groups = (set ?? "all").ToLowerInvariant() switch
        {
            "privileged" => WellKnownSecurityGroups.Privileged,
            "ordinary" => WellKnownSecurityGroups.OrdinaryAccounts,
            "all" => WellKnownSecurityGroups.All,
            _ => null
        };

        if (groups is null)
        {
            Console.Error.WriteLine($"Unknown --set '{set}'. Use privileged, ordinary or all.");
            return 2;
        }

        // label=SID, one per line, so a script can read it without a parser.
        foreach (var group in groups)
        {
            Console.WriteLine($"{group.Label}={group.Sid}");
        }

        return 0;
    }

    /// <summary>Checks a release manifest's shape and the bundle's integrity.</summary>
    internal static int ReleaseManifest(string? manifestPath, string? bundleRoot)
    {
        if (manifestPath is null)
        {
            Console.Error.WriteLine("release-manifest needs --in.");
            return 2;
        }

        var manifest = Core.Deployment.ReleaseManifest.Parse(Read(manifestPath));

        if (manifest is null)
        {
            Console.WriteLine("MANIFEST: unreadable.");
            return 1;
        }

        var problems = manifest.Problems();

        Console.WriteLine($"MANIFEST: {manifest.Product} {manifest.DisplayVersion} " +
                          $"({manifest.Architecture}, {manifest.BuildConfiguration})");
        Console.WriteLine($"  commit    : {manifest.GitSha}{(manifest.Dirty ? "  DIRTY" : "")}");
        Console.WriteLine($"  channel   : {manifest.Channel}");
        Console.WriteLine($"  signed    : {manifest.Signed}");
        Console.WriteLine($"  identity  : {manifest.PackageIdentity}");
        Console.WriteLine($"  components: {manifest.Components.Count}");

        foreach (var problem in problems)
        {
            Console.WriteLine($"  PROBLEM: {problem}");
        }

        var missing = manifest.MissingComponents();

        foreach (var component in missing)
        {
            Console.WriteLine($"  MISSING COMPONENT: {component}");
        }

        if (problems.Count > 0 || missing.Count > 0)
        {
            return 1;
        }

        if (bundleRoot is null)
        {
            Console.WriteLine("  (no --bundle given, so file digests were not checked)");
            return 0;
        }

        // Every file, hashed, before anything is installed. The manifest is the
        // list; the digests are what make the list mean something.
        var files = new PhysicalInstallFileSystem();
        var checked_ = 0;
        var bad = 0;

        foreach (var component in manifest.Components)
        {
            foreach (var file in component.Files)
            {
                var path = Path.Combine(bundleRoot, component.BundlePath, file.Path);

                if (!files.FileExists(path))
                {
                    Console.WriteLine($"  MISSING FILE: {component.BundlePath}\\{file.Path}");
                    bad++;
                    continue;
                }

                var actual = files.Sha256(path);
                checked_++;

                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  HASH MISMATCH: {component.BundlePath}\\{file.Path}");
                    bad++;
                }
                else if (files.Length(path) != file.Bytes)
                {
                    Console.WriteLine($"  LENGTH MISMATCH: {component.BundlePath}\\{file.Path}");
                    bad++;
                }
            }
        }

        Console.WriteLine($"  verified  : {checked_} file(s), {bad} problem(s)");

        return bad == 0 ? 0 : 1;
    }

    /// <summary>Decides whether this bundle may replace what is installed.</summary>
    internal static int PlanInstall(
        string? manifestPath, string? receiptPath, string? architecture,
        bool production, bool repair)
    {
        if (manifestPath is null)
        {
            Console.Error.WriteLine("plan-install needs --manifest.");
            return 2;
        }

        var manifest = Core.Deployment.ReleaseManifest.Parse(Read(manifestPath));
        var receipt = InstallReceipt.Parse(ReadOrNull(receiptPath));

        var decision = InstallPolicy.Decide(
            manifest, receipt, architecture ?? string.Empty, production, repair);

        Console.WriteLine($"PLAN: {decision.Action}");

        foreach (var explanation in decision.Explanations)
        {
            Console.WriteLine($"  {explanation}");
        }

        foreach (var refusal in decision.Refusals)
        {
            Console.WriteLine($"  REFUSED: {refusal}");
        }

        return decision.Allowed ? 0 : 1;
    }

    /// <summary>
    /// Lays the bundle down. Needs --apply.
    ///
    /// The install root is NOT taken from the command line as a free parameter:
    /// it must be exactly what <see cref="InstallationLayout"/> says, derived
    /// from the Program Files path given. A caller that could name any
    /// destination would make every path check in the transaction decorative.
    /// </summary>
    internal static int Install(
        string? manifestPath, string? bundleRoot, string? programFiles,
        string? receiptPath, string? programData, bool apply)
    {
        if (manifestPath is null || bundleRoot is null)
        {
            Console.Error.WriteLine("install needs --manifest and --bundle.");
            return 2;
        }

        var manifest = Core.Deployment.ReleaseManifest.Parse(Read(manifestPath));

        if (manifest is null)
        {
            Console.Error.WriteLine("The release manifest is unreadable.");
            return 1;
        }

        var programFilesPath = programFiles ??
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var programDataPath = programData ??
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var installRoot = InstallationLayout.InstallRoot(programFilesPath);
        var staging = installRoot + ".staging";

        Console.WriteLine($"INSTALL: {manifest.DisplayVersion} ({manifest.GitSha[..Math.Min(12, manifest.GitSha.Length)]})");
        Console.WriteLine($"  bundle      : {bundleRoot}");
        Console.WriteLine($"  install root: {installRoot}");
        Console.WriteLine($"  staging     : {staging}");

        if (!apply)
        {
            // DRY RUN. The description, and not one byte written.
            Console.WriteLine();
            Console.WriteLine("DRY RUN. Nothing will be written. Files that WOULD be installed:");

            foreach (var component in manifest.Components)
            {
                if (component.Component == KidShellComponent.App)
                {
                    Console.WriteLine($"  {component.Component}: the MSIX is registered, not copied");
                    continue;
                }

                var folder = InstallationLayout.FolderOf(component.Component);

                foreach (var file in component.Files)
                {
                    Console.WriteLine($"  {folder}\\{file.Path}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Nothing about the machine's security is changed by this, with or without --apply:");
            Console.WriteLine("no account, no service, no access list, no AppLocker, no shell.");

            return 0;
        }

        var files = new PhysicalInstallFileSystem();
        var previous = InstallReceipt.Parse(ReadOrNull(receiptPath));

        var outcome = new InstallTransaction(files)
            .Execute(manifest, bundleRoot, installRoot, staging);

        Console.WriteLine();

        foreach (var line in outcome.Log)
        {
            Console.WriteLine($"  {line}");
        }

        if (!outcome.Committed)
        {
            Console.WriteLine();
            Console.WriteLine(outcome.NeedsManualRecovery
                ? "FAILED, AND THE ROLLBACK DID NOT FINISH. Follow the recovery record."
                : "FAILED. Rolled back; nothing was left behind.");

            return outcome.NeedsManualRecovery ? 3 : 1;
        }

        var receipt = InstallReceipt.For(
            manifest, installRoot, outcome.WrittenFiles, DateTimeOffset.UtcNow, previous);

        var receiptFile = InstallationLayout.ReceiptFile(programDataPath);

        Directory.CreateDirectory(InstallationLayout.InstallationDirectory(programDataPath));
        File.WriteAllText(receiptFile, receipt.ToJson());

        Console.WriteLine();
        Console.WriteLine($"INSTALLED. Receipt: {receiptFile}");

        if (!manifest.Signed)
        {
            Console.WriteLine();
            Console.WriteLine("THIS INSTALLATION IS UNSIGNED. It is a dedicated-lab build:");
            Console.WriteLine("automatic updates stay off and nothing about it is a release.");
        }

        return 0;
    }

    /// <summary>Read-only check of an installation against its receipt.</summary>
    internal static int VerifyInstall(
        string? receiptPath, string? programFiles, string? programData, bool signatureChecked)
    {
        var programFilesPath = programFiles ??
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var programDataPath = programData ??
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var resolvedReceipt = receiptPath ?? InstallationLayout.ReceiptFile(programDataPath);
        var installRoot = InstallationLayout.InstallRoot(programFilesPath);

        var receipt = InstallReceipt.Parse(ReadOrNull(resolvedReceipt));
        var files = new PhysicalInstallFileSystem();

        var checks = InstallVerification.Check(receipt, files, installRoot, signatureChecked);
        var overall = InstallVerification.Overall(checks);

        Console.WriteLine($"INSTALLATION: {Label(overall)}");

        if (receipt is not null)
        {
            Console.WriteLine($"  version  : {receipt.DisplayVersion}");
            Console.WriteLine($"  commit   : {receipt.GitSha}");
            Console.WriteLine($"  channel  : {receipt.Channel}");
            Console.WriteLine($"  signed   : {receipt.Signed}");
            Console.WriteLine($"  identity : {receipt.PackageIdentity}");
        }

        foreach (var check in checks)
        {
            // Passes are summarised; anything else is printed, because the
            // things worth reading are the ones that are not fine.
            if (check.Status != InstallVerificationStatus.Pass)
            {
                Console.WriteLine($"  [{Label(check.Status)}] {check.Detail}");
            }
        }

        Console.WriteLine($"  {checks.Count(c => c.Status == InstallVerificationStatus.Pass)} check(s) passed.");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = Label(overall),
            version = receipt?.DisplayVersion ?? string.Empty,
            gitSha = receipt?.GitSha ?? string.Empty,
            signed = receipt?.Signed ?? false,
            channel = receipt?.Channel.ToString() ?? string.Empty,
            checks = checks.Select(c => new { status = Label(c.Status), c.Detail })
        }, Options));

        // Incomplete is not success. A build log must not show a finished-
        // looking verification that skipped the signature check.
        return overall == InstallVerificationStatus.Pass ? 0 : 1;
    }

    /// <summary>
    /// Removes what the receipt says was installed. Needs --apply.
    ///
    /// Exactly what the receipt lists, and nothing else. A file under the
    /// install root that the receipt does not mention is REPORTED and left -
    /// something put it there, and deleting it would be this tool guessing with
    /// administrator rights.
    ///
    /// It removes no ProgramData at all: the recovery manifests are how a
    /// parent undoes a security change, and the protected store may still hold
    /// the policy a device is running on.
    /// </summary>
    internal static int Uninstall(
        string? receiptPath, string? programFiles, string? programData, bool apply)
    {
        var programFilesPath = programFiles ??
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var programDataPath = programData ??
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var resolvedReceipt = receiptPath ?? InstallationLayout.ReceiptFile(programDataPath);
        var installRoot = InstallationLayout.InstallRoot(programFilesPath);

        var receipt = InstallReceipt.Parse(ReadOrNull(resolvedReceipt));

        if (receipt is null)
        {
            Console.WriteLine($"UNINSTALL: there is no receipt at {resolvedReceipt}.");
            Console.WriteLine("  Without one, what this product installed is not known, and nothing");
            Console.WriteLine("  will be removed. Remove the files by hand if you are sure.");

            return 1;
        }

        var files = new PhysicalInstallFileSystem();

        var known = receipt.Files.Keys
            .Select(relative => Path.Combine(installRoot, relative))
            .ToList();

        var present = files.EnumerateFiles(installRoot);

        var unknown = present
            .Where(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"UNINSTALL: {receipt.DisplayVersion} ({receipt.GitSha[..Math.Min(12, receipt.GitSha.Length)]})");
        Console.WriteLine($"  install root : {installRoot}");
        Console.WriteLine($"  in receipt   : {known.Count} file(s)");
        Console.WriteLine($"  not in receipt: {unknown.Count} file(s)");

        foreach (var path in unknown.Take(20))
        {
            Console.WriteLine($"    LEFT ALONE: {path}");
        }

        if (unknown.Count > 20)
        {
            Console.WriteLine($"    ... and {unknown.Count - 20} more");
        }

        Console.WriteLine();
        Console.WriteLine("  Preserved, and not touched by any uninstall:");

        foreach (var preserved in InstallationLayout.PreservedOnUninstall(programDataPath))
        {
            Console.WriteLine($"    {preserved}");
        }

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("DRY RUN. Nothing was removed.");
            return 0;
        }

        var removed = 0;
        var failed = new List<string>();

        foreach (var path in known)
        {
            try
            {
                if (files.FileExists(path))
                {
                    files.DeleteFile(path);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                failed.Add($"{path}: {ex.GetType().Name}");
            }
        }

        // Empty directories only, deepest first, and never recursively.
        foreach (var directory in known
                     .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
                     .Where(d => d.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                files.DeleteDirectoryIfEmpty(directory);
            }
            catch
            {
                // A directory that will not go is not a failed uninstall.
            }
        }

        Console.WriteLine();
        Console.WriteLine($"REMOVED {removed} file(s).");

        foreach (var failure in failed)
        {
            Console.WriteLine($"  COULD NOT REMOVE: {failure}");
        }

        var leftovers = files.EnumerateFiles(installRoot);

        if (leftovers.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{leftovers.Count} file(s) remain under {installRoot}:");

            foreach (var path in leftovers.Take(20))
            {
                Console.WriteLine($"  {path}");
            }
        }

        // The receipt goes last, so a failure above leaves something to retry
        // from.
        if (failed.Count == 0)
        {
            try
            {
                File.Delete(resolvedReceipt);
                Console.WriteLine();
                Console.WriteLine("Receipt removed. Evidence, recovery manifests and the protected store were kept.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"The receipt could not be removed: {ex.GetType().Name}");
            }
        }

        return failed.Count == 0 ? 0 : 1;
    }

    private static string Label(InstallVerificationStatus status) => status switch
    {
        InstallVerificationStatus.Pass => "PASS",
        InstallVerificationStatus.Fail => "FAIL",
        _ => "INCOMPLETE"
    };

    private static string Read(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException(path);

    private static string? ReadOrNull(string? path) =>
        path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
}
