using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.SecurityHost;

// ---------------------------------------------------------------------------
// KidShell elevated security helper.
//
// Reads one typed request per line from stdin, writes one typed response per
// line to stdout, and exits when stdin closes. It never parses a command
// string, never evaluates script, and never takes a path to execute from its
// caller.
//
// Run with --self-test to prove it starts and rejects malformed input without
// touching the machine. That is what CI runs; CI never runs it elevated and
// never sends it a mutating request.
// ---------------------------------------------------------------------------

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (arguments.Contains("--help") || arguments.Contains("-h"))
{
    Console.WriteLine("KidShell.SecurityHost - KidShells rättighetshjälpare.");
    Console.WriteLine();
    Console.WriteLine("  --self-test   Kontrollera att hjälparen startar. Ändrar ingenting.");
    Console.WriteLine("  --version     Visa version.");
    Console.WriteLine();
    Console.WriteLine("Utan flaggor läses typade begäranden som JSON från standard input.");
    return 0;
}

if (arguments.Contains("--version"))
{
    Console.WriteLine(KidShell.Core.Runtime.BuildInfo.Version);
    return 0;
}

var logger = new ConsoleErrorLogger();

if (arguments.Contains("--self-test"))
{
    // Deliberately does not require elevation and deliberately changes
    // nothing: this is a "does the binary work" check, not a security test.
    var selfTest = new HostSelfTest(logger);
    return await selfTest.RunAsync().ConfigureAwait(false) ? 0 : 1;
}

if (!IsElevated())
{
    // Better to say so on stderr and exit than to start accepting requests it
    // cannot fulfil. The manifest requests elevation, so reaching here means
    // something unusual happened.
    Console.Error.WriteLine("KidShell.SecurityHost måste köras som administratör.");
    return 2;
}

var dispatcher = new ElevatedDispatcher(logger);
return await dispatcher.RunAsync(Console.In, Console.Out, CancellationToken.None).ConfigureAwait(false);

static bool IsElevated()
{
    if (!OperatingSystem.IsWindows())
    {
        return false;
    }

    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>
/// Logs to stderr, keeping stdout clean for the protocol.
///
/// Mixing log output into stdout would corrupt the response stream, and the
/// caller would be parsing log lines as JSON.
/// </summary>
file sealed class ConsoleErrorLogger : IKidShellLogger
{
    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {level} {category}: {message}";
        Console.Error.WriteLine(exception is null ? line : $"{line} :: {exception.GetType().Name}");
    }
}
