using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;

namespace KidShell.Core.Tests;

/// <summary>Captures log lines so tests can assert that failures were recorded.</summary>
internal sealed class RecordingLogger : IKidShellLogger
{
    public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];

    public void Log(LogLevel level, string category, string message, Exception? exception = null) =>
        Entries.Add((level, category, message, exception));

    public bool HasError => Entries.Any(e => e.Level == LogLevel.Error);
}

/// <summary>A temporary directory that cleans itself up.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kidshell-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string ConfigPath => System.IO.Path.Combine(Path, "kidshell.config.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

/// <summary>Records launch attempts instead of starting anything.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Exception? _throwOnStart;

    public FakeProcessRunner(Exception? throwOnStart = null) => _throwOnStart = throwOnStart;

    public List<(string FileName, string Arguments, bool UseShellExecute)> Started { get; } = [];

    public void Start(string fileName, string arguments, bool useShellExecute)
    {
        if (_throwOnStart is not null)
        {
            throw _throwOnStart;
        }

        Started.Add((fileName, arguments, useShellExecute));
    }
}

/// <summary>Returns a canned resolution so launcher mapping can be tested in isolation.</summary>
internal sealed class StubResolver : IExecutableResolver
{
    private readonly Func<string, ExecutableResolution> _resolve;

    public StubResolver(ExecutableResolution result) => _resolve = _ => result;

    public StubResolver(Func<string, ExecutableResolution> resolve) => _resolve = resolve;

    public ExecutableResolution Resolve(string executablePath) => _resolve(executablePath);
}

internal sealed class StubDeveloperOptions : IDeveloperOptions
{
    public StubDeveloperOptions(bool developerMode) => DeveloperMode = developerMode;

    public bool DeveloperMode { get; }
}

internal static class TestFactory
{
    public static (AppStateService State, JsonConfigurationStore Store, RecordingLogger Logger) CreateState(TempDirectory dir)
    {
        var logger = new RecordingLogger();
        var store = new JsonConfigurationStore(dir.ConfigPath, logger);
        var state = new AppStateService(store, logger);
        return (state, store, logger);
    }

    public static KidAppDefinition App(
        string id = "demo",
        bool enabled = true,
        string path = "demo.exe",
        string arguments = "") => new()
        {
            Id = id,
            DisplayName = id,
            IsEnabled = enabled,
            ExecutablePath = path,
            Arguments = arguments
        };
}
