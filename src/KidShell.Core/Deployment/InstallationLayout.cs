namespace KidShell.Core.Deployment;

/// <summary>
/// The parts of KidShell that get installed.
///
/// A closed enum, because every path in the product is derived from it. The
/// alternative - each script knowing its own folder name - is how
/// build-release.ps1 came to copy from <c>bin\Release</c> while the build it
/// had just run produced <c>bin\x64\Release</c>: two places that each believed
/// they knew where the binaries were, and one of them was a week out of date.
/// </summary>
public enum KidShellComponent
{
    /// <summary>The shell itself. Packaged as MSIX.</summary>
    App = 0,

    /// <summary>The privileged broker, installed as a LocalSystem service.</summary>
    SecurityHost = 1,

    /// <summary>Restarts the shell if it stops.</summary>
    Watchdog = 2,

    /// <summary>What a recovery administrator runs when KidShell cannot start.</summary>
    Recovery = 3,

    /// <summary>The dedicated-device validation decision tool.</summary>
    DeviceValidation = 4
}

/// <summary>
/// THE ONE SOURCE OF TRUTH FOR WHERE KIDSHELL LIVES.
///
/// Every script, every validation probe, every installer step and every
/// expectation in the tests derives its paths from here. Nothing hard-codes a
/// second copy.
///
/// That rule exists because this repository has already been bitten twice by
/// two places disagreeing about a path: the release bundle copying stale
/// component output, and the validation scripts checking a location the
/// installer had never been asked to populate. Both were silent. A path that
/// only one half of the product believes in produces no error at all - it
/// produces a machine that looks installed.
///
/// NOTHING HERE TOUCHES A DISK. It composes strings and answers questions
/// about them.
/// </summary>
public static class InstallationLayout
{
    /// <summary>
    /// Where the binaries go, under Program Files.
    ///
    /// Program Files and not anywhere else: an ordinary account cannot write
    /// it, and a LocalSystem service whose image the child can replace is a
    /// privilege escalation with a service name.
    /// </summary>
    public const string ProgramFilesFolder = "KidShell";

    /// <summary>Where machine-wide state goes, under ProgramData.</summary>
    public const string ProgramDataFolder = "KidShell";

    /// <summary>The folder each component installs into, under the install root.</summary>
    public static string FolderOf(KidShellComponent component) => component switch
    {
        KidShellComponent.App => "KidShell.App",
        KidShellComponent.SecurityHost => "KidShell.SecurityHost",
        KidShellComponent.Watchdog => "KidShell.Watchdog",
        KidShellComponent.Recovery => "KidShell.Recovery",
        KidShellComponent.DeviceValidation => "KidShell.DeviceValidation",
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown component.")
    };

    /// <summary>
    /// The executable each component is identified by.
    ///
    /// The App has none here: it is an MSIX, registered rather than copied,
    /// and asking for its executable path is a category error that should not
    /// silently return something plausible.
    /// </summary>
    public static string? ExecutableOf(KidShellComponent component) => component switch
    {
        KidShellComponent.App => null,
        KidShellComponent.SecurityHost => "KidShell.SecurityHost.exe",
        KidShellComponent.Watchdog => "KidShell.Watchdog.exe",
        KidShellComponent.Recovery => "KidShell.Recovery.exe",
        KidShellComponent.DeviceValidation => "KidShell.DeviceValidation.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown component.")
    };

    /// <summary>
    /// The project each component is built from, and the only place a release
    /// bundle may take its binaries from.
    /// </summary>
    public static string ProjectOf(KidShellComponent component) => component switch
    {
        KidShellComponent.App => "KidShell.App",
        KidShellComponent.SecurityHost => "KidShell.SecurityHost",
        KidShellComponent.Watchdog => "KidShell.Watchdog",
        KidShellComponent.Recovery => "KidShell.Recovery",
        KidShellComponent.DeviceValidation => "KidShell.DeviceValidation",
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown component.")
    };

    /// <summary>
    /// Where a component's build output actually is.
    ///
    /// THE DEFECT THIS METHOD EXISTS TO END
    /// ------------------------------------
    /// build-release.ps1 built with <c>-p:Platform=x64</c> and then copied
    /// from <c>src\&lt;component&gt;\bin\Release\net10.0-windows</c>. MSBuild
    /// had written to <c>bin\x64\Release\net10.0-windows</c>. On a developer's
    /// machine the unplatformed folder existed from some earlier build, so the
    /// copy succeeded and shipped binaries a week older than the commit being
    /// released - including a SecurityHost from before the broker was
    /// rewritten. On a clean checkout the folder did not exist, the copy was
    /// skipped, and the bundle simply had no helper in it. Neither case
    /// produced an error.
    ///
    /// So the path is computed in one place, from the platform the build
    /// actually used, and the bundler fails rather than skips when it is not
    /// there.
    /// </summary>
    public static string BuildOutputOf(
        KidShellComponent component, string configuration, string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var framework = component == KidShellComponent.App
            ? "net10.0-windows10.0.26100.0"
            : "net10.0-windows";

        // AnyCPU is MSBuild's own spelling for "no platform subfolder".
        var platformSegment = platform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : platform + "\\";

        return $"src\\{ProjectOf(component)}\\bin\\{platformSegment}{configuration}\\{framework}";
    }

    /// <summary>Components a bundle must contain for a dedicated-device install.</summary>
    public static IReadOnlyList<KidShellComponent> Required =>
        [.. Enum.GetValues<KidShellComponent>()];

    /// <summary>
    /// Components the lab installer copies as files.
    ///
    /// The App is excluded: an MSIX is registered, not copied, and a file copy
    /// of a packaged app into Program Files would produce something that looks
    /// installed and will not start.
    /// </summary>
    public static IReadOnlyList<KidShellComponent> FileCopied =>
        [.. Enum.GetValues<KidShellComponent>().Where(c => c != KidShellComponent.App)];

    // --------------------------------------------------------- the paths

    public static string InstallRoot(string programFilesPath) =>
        Combine(programFilesPath, ProgramFilesFolder);

    public static string ComponentDirectory(string programFilesPath, KidShellComponent component) =>
        Combine(programFilesPath, ProgramFilesFolder, FolderOf(component));

    /// <summary>Where the release manifest and signing manifest are kept after install.</summary>
    public static string ManifestsDirectory(string programFilesPath) =>
        Combine(programFilesPath, ProgramFilesFolder, "manifests");

    public static string VersionFile(string programFilesPath) =>
        Combine(programFilesPath, ProgramFilesFolder, "version.json");

    public static string StateRoot(string programDataPath) =>
        Combine(programDataPath, ProgramDataFolder);

    /// <summary>The protected store. Permissioned, never created by KidShell itself.</summary>
    public static string PolicyDirectory(string programDataPath) =>
        Combine(programDataPath, ProgramDataFolder, "policy");

    public static string RecoveryDirectory(string programDataPath) =>
        Combine(programDataPath, ProgramDataFolder, "security", "recovery");

    public static string LogsDirectory(string programDataPath) =>
        Combine(programDataPath, ProgramDataFolder, "logs");

    /// <summary>Where the install receipt lives. The uninstaller reads it.</summary>
    public static string InstallationDirectory(string programDataPath) =>
        Combine(programDataPath, ProgramDataFolder, "installation");

    public static string ReceiptFile(string programDataPath) =>
        Combine(programDataPath, ProgramDataFolder, "installation", "install-receipt.json");

    /// <summary>
    /// ProgramData paths the uninstaller must NEVER delete.
    ///
    /// A recovery manifest is the record of how to undo a security change. An
    /// uninstall that removed it would take away the one thing a parent needs
    /// if the uninstall itself goes wrong, and the protected store may still
    /// hold the policy a device is running on. So an uninstall removes the
    /// program files and leaves these alone unless somebody asks explicitly.
    /// </summary>
    public static IReadOnlyList<string> PreservedOnUninstall(string programDataPath) =>
    [
        RecoveryDirectory(programDataPath),
        PolicyDirectory(programDataPath),
        LogsDirectory(programDataPath)
    ];

    // ------------------------------------------------------ path safety

    /// <summary>
    /// Whether a manifest's relative path is one the installer may write.
    ///
    /// A manifest is an untrusted document: it arrives on a USB stick with a
    /// bundle somebody built. Every destination in it is checked here before
    /// anything is copied, and the rule is positive rather than a blacklist -
    /// the path must be relative, must have no traversal segment, no root, no
    /// drive, no UNC prefix and no alternate data stream.
    ///
    /// Rejecting "..\" alone would be defence against the one spelling
    /// somebody thought of.
    /// </summary>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var value = relativePath;

        // NOT trimmed. The first version trimmed the whole string, which
        // stripped a trailing space off the LAST segment and let "evil.dll "
        // through - and Win32 strips that space too, so it names the same file
        // as "evil.dll" while being a different string to anything comparing
        // them. A manifest a build produced has no stray whitespace in it; one
        // that does is worth refusing rather than tidying.
        if (value != value.Trim())
        {
            return false;
        }

        if (value.Any(char.IsControl))
        {
            return false;
        }

        // A drive, a root, or a UNC path is not relative.
        if (value.Contains(':') || value.StartsWith('/') || value.StartsWith('\\'))
        {
            return false;
        }

        var segments = value.Split('\\', '/');

        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                // An empty segment means "\\" somewhere in the middle, which
                // normalises to something other than what it reads as.
                return false;
            }

            if (segment is "." or "..")
            {
                return false;
            }

            // A trailing dot or space is stripped by Win32, so "foo." and
            // "foo" are the same file while being different strings - which
            // is a way past a comparison somewhere else.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return false;
            }

            if (segment.Contains(':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a resolved destination is inside the install root.
    ///
    /// Checked in addition to <see cref="IsSafeRelativePath"/> and not instead
    /// of it: the first is about the string a manifest supplied, this is about
    /// where that string actually landed once combined. Both have to hold,
    /// because a symlink or a short name can make a safe-looking relative path
    /// resolve somewhere else.
    /// </summary>
    public static bool IsInsideRoot(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalisedRoot = Normalise(root).TrimEnd('\\') + "\\";
        var normalisedCandidate = Normalise(candidate);

        return normalisedCandidate.StartsWith(normalisedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string path) =>
        path.Replace('/', '\\').Replace("\\\\", "\\");

    /// <summary>
    /// Path building without System.IO.Path, so the layout is the same
    /// whatever host computes it. The separator is Windows' because the target
    /// is.
    /// </summary>
    private static string Combine(string root, params string[] parts) =>
        string.Join('\\', new[] { (root ?? string.Empty).TrimEnd('\\', '/') }.Concat(parts));
}
