namespace KidShell.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// Minimal local-only logging surface. KidShell sends nothing anywhere:
/// no telemetry, no analytics, no network calls.
/// </summary>
public interface IKidShellLogger
{
    void Log(LogLevel level, string category, string message, Exception? exception = null);
}

public static class KidShellLoggerExtensions
{
    public static void Debug(this IKidShellLogger logger, string category, string message) =>
        logger.Log(LogLevel.Debug, category, message);

    public static void Info(this IKidShellLogger logger, string category, string message) =>
        logger.Log(LogLevel.Info, category, message);

    public static void Warning(this IKidShellLogger logger, string category, string message, Exception? ex = null) =>
        logger.Log(LogLevel.Warning, category, message, ex);

    public static void Error(this IKidShellLogger logger, string category, string message, Exception? ex = null) =>
        logger.Log(LogLevel.Error, category, message, ex);
}

/// <summary>Logger that discards everything. Useful in tests.</summary>
public sealed class NullLogger : IKidShellLogger
{
    public static readonly NullLogger Instance = new();

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}
