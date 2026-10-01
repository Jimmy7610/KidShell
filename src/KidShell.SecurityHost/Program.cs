using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;
using KidShell.SecurityHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

// ---------------------------------------------------------------------------
// KidShell security host.
//
// Three ways to run, and only one of them is the product:
//
//   (default)          the Windows service. Holds the privilege, listens on
//                      one named pipe, and answers typed requests from
//                      callers it identifies from their Windows token.
//
//   --approve-policy   one elevated commit. What a parent's consent prompt
//                      runs when they save settings: it connects to the
//                      service as an administrator and turns the staged
//                      policy into the live one. Exits 0 if it did.
//
//   --self-test        proves the binary works, installs nothing, starts
//                      nothing and changes nothing. What CI runs.
//
// WHAT CHANGED IN THIS BUILD
// --------------------------
// It used to read one request per line from stdin, and KidShell used to
// start it per request without elevating it. The helper therefore refused
// every request on every real child account, and the comment claiming it was
// launched with the "runas" verb described something the code never did.
// See docs/PRIVILEGED-BROKER-SERVICE-2026-09-30.md.
// ---------------------------------------------------------------------------

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (arguments.Contains("--help") || arguments.Contains("-h"))
{
    Console.WriteLine("KidShell.SecurityHost - KidShells rättighetstjänst.");
    Console.WriteLine();
    Console.WriteLine("  --self-test        Kontrollera tjänsten. Installerar och startar ingenting.");
    Console.WriteLine("  --approve-policy   Godkänn en väntande ändring. Kräver administratör.");
    Console.WriteLine("  --version          Visa version.");
    Console.WriteLine();
    Console.WriteLine("Installeras av KidShell som en Windows-tjänst. Kör den inte för hand.");
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

if (arguments.Contains(ElevatedCommitApprovalChannel.ApproveArgument))
{
    return ApprovePolicy(arguments, logger);
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("KidShell.SecurityHost körs bara på Windows.");
    return 3;
}

if (!IsElevated())
{
    // The service runs as LocalSystem, so reaching here unelevated means
    // somebody started the binary by hand. Better to say so and exit than to
    // open an endpoint that cannot do the work behind it.
    Console.Error.WriteLine("KidShell.SecurityHost måste köras som tjänst eller som administratör.");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = SecurityHostService.Name);
builder.Services.AddSingleton<IKidShellLogger>(logger);
builder.Services.AddHostedService<BrokerWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

/// <summary>
/// Turns the staged policy into the live one, as an administrator.
///
/// This process has already passed a consent prompt by the time it runs -
/// the unprivileged side launched it with the runas verb, which is where
/// that verb genuinely belongs: once, when a parent saves settings, not on
/// a thirty-second timer.
///
/// It carries only a digest. The policy itself is read by the service from
/// the staging slot, so what is approved is what is stored rather than
/// whatever the launching process chose to pass along.
/// </summary>
static int ApprovePolicy(string[] arguments, IKidShellLogger log)
{
    var index = Array.IndexOf(arguments, ElevatedCommitApprovalChannel.ApproveArgument);

    if (index < 0 || index + 1 >= arguments.Length)
    {
        Console.Error.WriteLine("Ingen kontrollsumma angavs.");
        return 2;
    }

    if (!IsElevated())
    {
        Console.Error.WriteLine("Godkännandet kräver administratörsbehörighet.");
        return 2;
    }

    var client = new NamedPipeElevatedBrokerClient(BrokerEndpoint.PipeName, log);

    var response = client.Send(new ElevatedRequest
    {
        Kind = ElevatedOperationKind.CommitStagedParentPolicy,
        RequestId = Guid.NewGuid().ToString("n"),
        ExpectedDigest = arguments[index + 1],
        DryRun = false
    });

    if (response.Success)
    {
        return 0;
    }

    Console.Error.WriteLine(response.Message);
    return 1;
}

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
/// Logs to stderr, keeping stdout clean.
///
/// Under the service control manager nothing reads either, and the lines go
/// to the Windows event log through the hosting integration. Keeping the
/// separation anyway costs nothing and means a hand-started diagnostic run
/// still behaves.
/// </summary>
file sealed class ConsoleErrorLogger : IKidShellLogger
{
    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {level} {category}: {message}";
        Console.Error.WriteLine(exception is null ? line : $"{line} :: {exception.GetType().Name}");
    }
}
