using KidShell.Core.Diagnostics;
using KidShell.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// KidShell watchdog service.
//
// Restarts KidShell in a child's session if it stops. That is all it does.
//
// It reads no configuration file, opens no pipe or socket, and accepts no
// commands. The AUMID it launches is compiled in rather than configurable,
// because a LocalSystem service that starts whatever a writable file names is
// a privilege escalation, not a watchdog.
//
// --self-test proves the binary runs and its crash-loop logic behaves, without
// installing, starting or launching anything. That is what CI runs.
// ---------------------------------------------------------------------------

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (arguments.Contains("--help") || arguments.Contains("-h"))
{
    Console.WriteLine("KidShell.Watchdog - startar om KidShell om det stängs.");
    Console.WriteLine();
    Console.WriteLine("  --self-test   Kontrollera tjänsten. Installerar och startar ingenting.");
    Console.WriteLine("  --version     Visa version.");
    Console.WriteLine();
    Console.WriteLine("Installeras av KidShell som en Windows-tjänst. Kör den inte för hand.");
    return 0;
}

if (arguments.Contains("--version"))
{
    Console.WriteLine(KidShell.Core.Runtime.BuildInfo.Version);
    return 0;
}

if (arguments.Contains("--self-test"))
{
    return WatchdogSelfTest.Run() ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "KidShellWatchdog");

builder.Services.AddSingleton<IKidShellLogger>(_ => new NullLogger());

builder.Services.AddSingleton<IShellStarter>(sp =>
    OperatingSystem.IsWindows()
        ? new WindowsShellStarter(
            WatchdogLaunch.Command,
            sp.GetRequiredService<ILogger<WindowsShellStarter>>())
        : throw new PlatformNotSupportedException("The KidShell watchdog runs on Windows only."));

builder.Services.AddHostedService<WatchdogWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

/// <summary>
/// How KidShell is launched. A constant, deliberately.
///
/// Making this configurable would mean the service starts whatever a file
/// says - and the child's account can write files. The AUMID is fixed at build
/// time and matches the package identity in Package.appxmanifest.
/// </summary>
file static class WatchdogLaunch
{
    private const string PackageFamilyName = "KidShell.Barnlage.Dev_8wekyb3d8bbwe";

    public static string Command =>
        $@"{Environment.GetFolderPath(Environment.SpecialFolder.Windows)}\explorer.exe shell:AppsFolder\{PackageFamilyName}!App";
}

/// <summary>
/// Checks the watchdog's decision logic without installing or starting
/// anything.
/// </summary>
file static class WatchdogSelfTest
{
    public static bool Run()
    {
        var failures = new List<string>();
        var monitor = new KidShell.Core.Watchdog.ShellHealthMonitor(new NullLogger());

        // A fresh monitor should want to restart, not give up.
        var first = monitor.Evaluate();

        if (first.Action == KidShell.Core.Watchdog.WatchdogAction.ShowFailureScreen)
        {
            failures.Add("a healthy shell should not be treated as crash looping");
        }

        // After enough rapid restarts it must stop trying rather than flicker
        // at a child forever.
        for (var i = 0; i < KidShell.Core.Watchdog.ShellHealthMonitor.CrashLoopThreshold + 1; i++)
        {
            monitor.RecordRestart();
        }

        if (monitor.Evaluate().Action != KidShell.Core.Watchdog.WatchdogAction.ShowFailureScreen)
        {
            failures.Add("rapid restarts should be detected as a crash loop");
        }

        // The launch command must name explorer plus an AppsFolder target, and
        // nothing else - no arbitrary path.
        if (!WatchdogLaunch.Command.Contains("shell:AppsFolder", StringComparison.Ordinal))
        {
            failures.Add("the launch command should use the documented AppsFolder route");
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"FAIL: {failure}");
        }

        if (failures.Count > 0)
        {
            return false;
        }

        Console.WriteLine("KidShell.Watchdog self-test passed. Nothing was installed or started.");
        return true;
    }
}

/// <summary>Discards log lines. The service logs through ILogger instead.</summary>
file sealed class NullLogger : IKidShellLogger
{
    public void Log(
        KidShell.Core.Diagnostics.LogLevel level,
        string category,
        string message,
        Exception? exception = null)
    {
    }
}
