using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KidShell.Core.Watchdog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidShell.Watchdog;

/// <summary>Starts KidShell in a user's interactive session.</summary>
public interface IShellStarter
{
    /// <summary>The sessions with a signed-in user, if any.</summary>
    IReadOnlyList<uint> ActiveSessions();

    /// <summary>Whether KidShell is already running in that session.</summary>
    bool IsRunning(uint sessionId);

    /// <summary>Launches KidShell in that session. Returns false if it could not.</summary>
    bool Start(uint sessionId);
}

/// <summary>
/// The watchdog service loop.
///
/// HOW IT DECIDES
/// --------------
/// It polls for KidShell's presence in each interactive session. There is
/// deliberately NO heartbeat channel: a pipe or a socket the child's session
/// can write to is an input to a LocalSystem process, and the whole reason this
/// service is safe is that it has no inputs. Process presence is a signal the
/// child cannot forge into a command.
///
/// Crash-loop handling comes from <see cref="ShellHealthMonitor"/>, which is
/// already tested in KidShell.Core. After a few rapid restarts the watchdog
/// stops trying and leaves the calm failure state in place, because a child
/// watching a window flicker forever is worse off than one who can go and ask
/// an adult.
///
/// It never falls back to showing the desktop. That would make crashing
/// KidShell the easiest way out of KidShell.
/// </summary>
public sealed class WatchdogWorker : BackgroundService
{
    /// <summary>How often presence is checked. Slow on purpose: this is not a race.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IShellStarter _starter;
    private readonly ILogger<WatchdogWorker> _logger;
    private readonly Dictionary<uint, ShellHealthMonitor> _monitors = [];
    private readonly Core.Diagnostics.IKidShellLogger _shellLogger;

    public WatchdogWorker(
        IShellStarter starter,
        ILogger<WatchdogWorker> logger,
        Core.Diagnostics.IKidShellLogger shellLogger)
    {
        _starter = starter;
        _logger = logger;
        _shellLogger = shellLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("KidShell watchdog started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TickOnce();
            }
            catch (Exception ex)
            {
                // A failure in one poll must not take the service down; the
                // next poll may well succeed.
                _logger.LogError(ex, "Watchdog poll failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("KidShell watchdog stopping.");
    }

    /// <summary>One poll. Exposed so the loop can be tested without a service host.</summary>
    internal void TickOnce()
    {
        foreach (var session in _starter.ActiveSessions())
        {
            if (!_monitors.TryGetValue(session, out var monitor))
            {
                monitor = new ShellHealthMonitor(_shellLogger);
                _monitors[session] = monitor;
            }

            if (_starter.IsRunning(session))
            {
                monitor.Heartbeat();
                continue;
            }

            var decision = monitor.Evaluate();

            if (decision.Action == WatchdogAction.ShowFailureScreen)
            {
                // Crash looping. Stop restarting and leave the session alone -
                // and specifically do NOT start Explorer or anything else as a
                // fallback.
                _logger.LogWarning("Session {Session}: {Reason}", session, decision.Reason);
                continue;
            }

            _logger.LogWarning("Session {Session}: KidShell is not running. Restarting.", session);

            if (_starter.Start(session))
            {
                monitor.RecordRestart();
            }
        }
    }
}

/// <summary>
/// Starts KidShell in an interactive session from a LocalSystem service.
///
/// A service runs in session 0 and cannot simply start a process a user will
/// see. The documented route is to take the session's user token with
/// <c>WTSQueryUserToken</c> and create the process with
/// <c>CreateProcessAsUser</c>, so KidShell runs as the child, in the child's
/// session, with the child's rights - never as LocalSystem.
///
/// That last part matters: a shell started with the service's own token would
/// hand the child a window running as SYSTEM.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellStarter : IShellStarter
{
    private const string ProcessName = "KidShell";

    private readonly string _launchCommand;
    private readonly ILogger<WindowsShellStarter> _logger;

    public WindowsShellStarter(string launchCommand, ILogger<WindowsShellStarter> logger)
    {
        _launchCommand = launchCommand;
        _logger = logger;
    }

    public IReadOnlyList<uint> ActiveSessions()
    {
        var sessions = new List<uint>();
        var buffer = IntPtr.Zero;

        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buffer, out var count))
            {
                return sessions;
            }

            var size = Marshal.SizeOf<WtsSessionInfo>();

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * size));

                // WTSActive only. A disconnected or listening session has no
                // desktop to start anything on.
                if (info.State == WtsConnectStateClass.WTSActive && info.SessionId != 0)
                {
                    sessions.Add(info.SessionId);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }

        return sessions;
    }

    public bool IsRunning(uint sessionId)
    {
        try
        {
            return Process.GetProcessesByName(ProcessName)
                .Any(p => (uint)p.SessionId == sessionId);
        }
        catch
        {
            // If presence cannot be determined, assume it is running. Starting
            // a second copy is worse than skipping one poll.
            return true;
        }
    }

    public bool Start(uint sessionId)
    {
        var token = IntPtr.Zero;
        var environment = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken(sessionId, out token))
            {
                _logger.LogWarning("Could not obtain the user token for session {Session}.", sessionId);
                return false;
            }

            if (!CreateEnvironmentBlock(out environment, token, inherit: false))
            {
                environment = IntPtr.Zero;
            }

            var startupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),

                // The interactive desktop of that session. Without this the
                // process starts with no window station and the child sees
                // nothing.
                lpDesktop = @"winsta0\default"
            };

            var created = CreateProcessAsUser(
                token,
                null,
                _launchCommand,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: CreateUnicodeEnvironment | CreateNewConsole,
                lpEnvironment: environment,
                lpCurrentDirectory: null,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out var processInformation);

            if (!created)
            {
                _logger.LogWarning("CreateProcessAsUser failed with {Error}.", Marshal.GetLastWin32Error());
                return false;
            }

            CloseHandle(processInformation.hProcess);
            CloseHandle(processInformation.hThread);
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    // ------------------------------------------------------------- interop

    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewConsole = 0x00000010;

    private enum WtsConnectStateClass
    {
        WTSActive = 0,
        WTSConnected = 1,
        WTSConnectQuery = 2,
        WTSShadow = 3,
        WTSDisconnected = 4,
        WTSIdle = 5,
        WTSListen = 6,
        WTSReset = 7,
        WTSDown = 8,
        WTSInit = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WtsConnectStateClass State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
