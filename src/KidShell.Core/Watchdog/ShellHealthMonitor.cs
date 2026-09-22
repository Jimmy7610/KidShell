using KidShell.Core.Diagnostics;

namespace KidShell.Core.Watchdog;

/// <summary>What the watchdog thinks is going on.</summary>
public enum ShellHealth
{
    /// <summary>Heartbeats arriving on time.</summary>
    Healthy = 0,

    /// <summary>No heartbeat for longer than expected, but not yet dead.</summary>
    Unresponsive = 1,

    /// <summary>The shell stopped and should be restarted.</summary>
    Stopped = 2,

    /// <summary>
    /// Restarted too many times too quickly. Restarting again would just
    /// flicker at the child, so the watchdog stops and shows a calm screen.
    /// </summary>
    CrashLooping = 3
}

/// <summary>What the watchdog decided to do.</summary>
public enum WatchdogAction
{
    None = 0,
    Restart = 1,

    /// <summary>Stop trying and show the child a calm "ask an adult" screen.</summary>
    ShowFailureScreen = 2
}

public sealed record WatchdogDecision(ShellHealth Health, WatchdogAction Action, string Reason);

/// <summary>
/// Restarts the child shell when it dies.
///
/// Declared here; the implementation that actually starts a process belongs to
/// a later milestone, and the Windows service host is deliberately not built.
/// Nothing in this milestone installs a service, and nothing may.
/// </summary>
public interface IWatchdog
{
    Task<WatchdogDecision> EvaluateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Tracks heartbeats and decides whether to restart.
///
/// Pure logic, no process handling, so the crash-loop rules can be tested
/// without killing anything.
///
/// The crash-loop rule is the important part. A shell that fails immediately
/// on startup - a corrupt configuration, a missing dependency - would
/// otherwise be restarted forever, producing a window that flickers at a child
/// indefinitely. After a few rapid failures the watchdog stops and shows a
/// calm screen instead, because a stuck child who can ask for help is in a
/// better position than one watching a strobe.
///
/// It deliberately does NOT fall back to the Windows desktop. Dropping a child
/// onto the desktop as an error path would make crashing the shell the easiest
/// way out of it.
/// </summary>
public sealed class ShellHealthMonitor : IWatchdog
{
    /// <summary>Restarts within this window count towards a crash loop.</summary>
    public static readonly TimeSpan CrashLoopWindow = TimeSpan.FromMinutes(2);

    /// <summary>Restarts inside the window before the watchdog gives up.</summary>
    public const int CrashLoopThreshold = 3;

    /// <summary>Missed heartbeat time before the shell is considered unresponsive.</summary>
    public static readonly TimeSpan UnresponsiveAfter = TimeSpan.FromSeconds(30);

    /// <summary>Missed heartbeat time before the shell is considered stopped.</summary>
    public static readonly TimeSpan StoppedAfter = TimeSpan.FromSeconds(90);

    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;
    private readonly List<DateTimeOffset> _restarts = [];

    private DateTimeOffset _lastHeartbeat;

    public ShellHealthMonitor(IKidShellLogger logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _lastHeartbeat = _time.GetUtcNow();
    }

    /// <summary>Restarts recorded inside the crash-loop window.</summary>
    public int RecentRestarts => CountRecentRestarts();

    /// <summary>The shell says it is alive.</summary>
    public void Heartbeat() => _lastHeartbeat = _time.GetUtcNow();

    /// <summary>Records that the shell was restarted.</summary>
    public void RecordRestart()
    {
        var now = _time.GetUtcNow();
        _restarts.Add(now);
        _lastHeartbeat = now;

        _logger.Info("Watchdog", $"Shell restarted; {CountRecentRestarts()} restart(s) in the last {CrashLoopWindow.TotalMinutes:F0} minutes.");
    }

    /// <summary>Clears the crash-loop history, e.g. after a parent intervened.</summary>
    public void Reset()
    {
        _restarts.Clear();
        _lastHeartbeat = _time.GetUtcNow();
    }

    public Task<WatchdogDecision> EvaluateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate());

    public WatchdogDecision Evaluate()
    {
        // Crash looping is checked first: a shell that keeps dying on startup
        // does send heartbeats, briefly, and would otherwise look healthy
        // between crashes.
        if (CountRecentRestarts() >= CrashLoopThreshold)
        {
            return new WatchdogDecision(
                ShellHealth.CrashLooping,
                WatchdogAction.ShowFailureScreen,
                $"KidShell startade om {CountRecentRestarts()} gånger på kort tid.");
        }

        var silence = _time.GetUtcNow() - _lastHeartbeat;

        if (silence >= StoppedAfter)
        {
            return new WatchdogDecision(
                ShellHealth.Stopped,
                WatchdogAction.Restart,
                "KidShell svarar inte och startas om.");
        }

        if (silence >= UnresponsiveAfter)
        {
            return new WatchdogDecision(
                ShellHealth.Unresponsive,
                WatchdogAction.None,
                "KidShell har inte hört av sig på en stund.");
        }

        return new WatchdogDecision(ShellHealth.Healthy, WatchdogAction.None, string.Empty);
    }

    private int CountRecentRestarts()
    {
        var cutoff = _time.GetUtcNow() - CrashLoopWindow;
        _restarts.RemoveAll(r => r < cutoff);
        return _restarts.Count;
    }

    /// <summary>
    /// What the child sees when the watchdog gives up. Calm, short, and it
    /// does not suggest the desktop as a way out.
    /// </summary>
    public const string FailureHeadline = "Något gick fel.";

    public const string FailureBody = "Be en vuxen om hjälp.";
}
