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
