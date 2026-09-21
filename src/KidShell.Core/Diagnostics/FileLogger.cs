using System.Text;

namespace KidShell.Core.Diagnostics;

/// <summary>
/// Appends plain-text lines to a rolling local log file under the KidShell
/// data directory. Deliberately dependency-free and failure-tolerant: logging
/// must never be the reason the app breaks.
/// </summary>
public sealed class FileLogger : IKidShellLogger
{
    private const long MaxBytes = 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly LogLevel _minimumLevel;

    public FileLogger(string path, LogLevel minimumLevel = LogLevel.Debug)
    {
        _path = path;
        _minimumLevel = minimumLevel;
    }

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(category).Append(" | ").Append(message);

        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        }

        line.AppendLine();

        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RollIfNeeded();
                File.AppendAllText(_path, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // A failing log write is never worth surfacing to a six-year-old.
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        var previous = _path + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(_path, previous);
    }
}
