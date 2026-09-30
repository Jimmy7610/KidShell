using Windows.Storage;

namespace KidShell.App.Services;

/// <summary>
/// Where KidShell keeps its per-user data.
///
/// Never Program Files: a packaged app writes to its own LocalState folder,
/// and an unpackaged debug run falls back to %LOCALAPPDATA%\KidShell.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> DataDirectoryLazy = new(ResolveDataDirectory);

    public static string DataDirectory => DataDirectoryLazy.Value;

    public static string ConfigurationFilePath => Path.Combine(DataDirectory, "kidshell.config.json");

    public static string LogFilePath => Path.Combine(DataDirectory, "logs", "kidshell.log");

    /// <summary>
    /// The screen-time counter. Its own file because it is written every tick,
    /// while the configuration is written when a parent saves - sharing one
    /// would let a routine counter update corrupt the parent's settings.
    /// </summary>
    public static string ScreenTimeStatePath => Path.Combine(DataDirectory, "screentime.json");

    /// <summary>
    /// Recovery manifests. A folder rather than a file: each transaction gets
    /// its own, and they outlive the transaction that wrote them.
    ///
    /// MACHINE-WIDE, NOT IN LOCALSTATE
    /// -------------------------------
    /// These used to live beside the configuration, in the package's
    /// LocalState. That is inside the profile of whichever account ran
    /// KidShell - which, on a locked-down machine, is the child's. The entire
    /// purpose of a recovery manifest is that a DIFFERENT administrator can
    /// read it when KidShell will not start, and a file inside an unreachable
    /// profile cannot be read at the moment it is needed.
    ///
    /// The elevated helper and KidShell.Recovery.exe both use this path, so all
    /// three agree on where the files are. They disagreed once, which meant the
    /// Säkerhet page told a parent to look somewhere the tool would not.
    /// </summary>
    public static string RecoveryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidShell", "security", "recovery");

    /// <summary>
    /// The protected policy store: %ProgramData%\KidShell\policy.
    ///
    /// Machine-wide rather than per-user, because the whole point is that it
    /// does NOT belong to the signed-in child. KidShell never creates it -
    /// a directory created here would be owned by whoever ran KidShell, which
    /// on a locked-down machine is the child, and that is a store that looks
    /// like protection and is not. An elevated provisioning step creates and
    /// permissions it; see ProtectedStorePlan.
    /// </summary>
    public static string ProtectedPolicyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidShell", "policy");

    /// <summary>
    /// The development stand-in for the protected store. In the signed-in
    /// user's own profile, and therefore not protected at all - which is why
    /// only a development build is allowed to use it.
    /// </summary>
    public static string DevelopmentPolicyDirectory => Path.Combine(DataDirectory, "policy");

    /// <summary>
    /// The elevated helper, beside KidShell in the package.
    ///
    /// Nothing starts it unless a protected write is attempted, and Windows
    /// will not elevate it without a prompt. On a machine where it is absent
    /// the broker reports itself unavailable and a production build fails
    /// closed, which is the correct behaviour rather than a degraded one.
    /// </summary>
    public static string SecurityHostPath => Path.Combine(
        AppContext.BaseDirectory, "KidShell.SecurityHost.exe");

    /// <summary>
    /// Where generated policy artifacts are written for review. Nothing here
    /// is ever applied; it exists so a parent can read what KidShell would do.
    /// </summary>
    public static string ArtifactsDirectory => Path.Combine(DataDirectory, "artifacts");

    private static string ResolveDataDirectory()
    {
        string directory;

        try
        {
            directory = ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception)
        {
            // No package identity (unpackaged debugging): use the ordinary
            // per-user application data location instead.
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KidShell");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }
}
