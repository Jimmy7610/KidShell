using System.Diagnostics;

namespace KidShell.Core.Launching;

/// <summary>
/// The one place in KidShell that actually calls Process.Start. Everything
/// else goes through <see cref="IAppLauncher"/>.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public void Start(string fileName, string arguments, bool useShellExecute)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            // ShellExecute is required for protocol/app activation and is also
            // what gives Store-app stubs such as calc.exe their normal
            // behaviour. KidShell never runs anything elevated.
            UseShellExecute = useShellExecute || string.IsNullOrEmpty(Path.GetDirectoryName(fileName))
        };

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            info.Arguments = arguments;
        }

        var workingDirectory = Path.GetDirectoryName(fileName);
        if (!info.UseShellExecute && !string.IsNullOrEmpty(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }

        var process = Process.Start(info);

        if (process is null && !info.UseShellExecute)
        {
            // A null handle is normal for shell activation, but not for a
            // plain executable: treat that as a failed launch.
            throw new InvalidOperationException($"Process.Start returned no process for '{fileName}'.");
        }

        process?.Dispose();
    }
}
