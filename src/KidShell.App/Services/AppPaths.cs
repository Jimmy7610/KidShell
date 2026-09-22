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
    /// </summary>
    public static string RecoveryDirectory => Path.Combine(DataDirectory, "recovery");

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
