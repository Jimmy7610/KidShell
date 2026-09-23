using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Transactions;
using KidShell.Recovery;

// ---------------------------------------------------------------------------
// KidShell recovery tool.
//
// For a recovery administrator on a bad day: KidShell will not start, or a
// security change went wrong, and somebody needs to know what was done and how
// to undo it.
//
// It reads the typed recovery manifests KidShell wrote BEFORE it changed
// anything. It does not read the child's configuration, does not need KidShell
// to run, and takes no command from anywhere - the arguments select a manifest,
// they never describe an action to perform.
// ---------------------------------------------------------------------------

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (arguments.Length == 0 || arguments.Contains("--help") || arguments.Contains("-h"))
{
    Console.WriteLine("KidShell.Recovery - visar och ångrar KidShells säkerhetsändringar.");
    Console.WriteLine();
    Console.WriteLine("  --list               Visa alla registrerade ändringar.");
    Console.WriteLine("  --outstanding        Visa bara de som behöver åtgärdas.");
    Console.WriteLine("  --show <id>          Visa en ändring i detalj, med steg för steg.");
    Console.WriteLine("  --export <fil>       Spara instruktionerna till en textfil.");
    Console.WriteLine("  --restore <id>       Ångra en ändring (kräver administratör).");
    Console.WriteLine("  --path <mapp>        Läs återställningsfiler från en annan mapp.");
    Console.WriteLine("  --self-test          Kontrollera verktyget. Ändrar ingenting.");
    Console.WriteLine();
    Console.WriteLine("Återställningsfilerna ligger normalt i:");
    Console.WriteLine($"  {RecoveryPaths.Default}");
    return 0;
}

if (arguments.Contains("--self-test"))
{
    return RecoverySelfTest.Run() ? 0 : 1;
}

var directory = ValueAfter(arguments, "--path") ?? RecoveryPaths.Default;
var logger = new ConsoleLogger();
var store = new RecoveryManifestStore(directory, logger);

if (!Directory.Exists(directory))
{
    Console.WriteLine($"Det finns inga återställningsfiler i {directory}.");
    Console.WriteLine("Det betyder normalt att KidShell aldrig har ändrat något på den här datorn.");
    return 0;
}

if (arguments.Contains("--list") || arguments.Contains("--outstanding"))
{
    var onlyOutstanding = arguments.Contains("--outstanding");

    var manifests = onlyOutstanding
        ? await store.ListOutstandingAsync().ConfigureAwait(false)
        : await store.ListAsync().ConfigureAwait(false);

    if (manifests.Count == 0)
    {
        Console.WriteLine(onlyOutstanding
            ? "Inga ändringar behöver åtgärdas."
            : "Inga ändringar finns registrerade.");
        return 0;
    }

    Console.WriteLine($"{manifests.Count} ändring(ar):");
    Console.WriteLine();

    foreach (var manifest in manifests)
    {
        Console.WriteLine("  " + RecoveryReport.Summarize(manifest));
    }

    var needing = manifests.Count(RecoveryReport.NeedsAttention);

    if (needing > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{needing} av dem behöver åtgärdas. Kör --show <id> för instruktioner.");
    }

    return 0;
}

var showId = ValueAfter(arguments, "--show") ?? ValueAfter(arguments, "--restore");

if (showId is null)
{
    Console.Error.WriteLine("Ange --list, --outstanding, --show <id> eller --restore <id>.");
    return 1;
}

var all = await store.ListAsync().ConfigureAwait(false);

var chosen = all.FirstOrDefault(m =>
    string.Equals(m.TransactionId, showId, StringComparison.OrdinalIgnoreCase));

if (chosen is null)
{
    Console.Error.WriteLine($"Ingen ändring med id \"{showId}\" hittades.");
    Console.Error.WriteLine("Kör --list för att se vilka som finns.");
    return 1;
}

var report = RecoveryReport.Describe(chosen);
Console.WriteLine(report);

if (ValueAfter(arguments, "--export") is { } exportPath)
{
    await File.WriteAllTextAsync(exportPath, report).ConfigureAwait(false);
    Console.WriteLine($"Instruktionerna sparades till {exportPath}.");
}

if (arguments.Contains("--restore"))
{
    // Automatic restore runs the same typed rollback the transaction would
    // have run, through the same coordinator. It cannot execute in this build,
    // because no KidShell build can construct an Apply context - so the tool
    // says so plainly rather than appearing to work.
    Console.WriteLine();
    Console.WriteLine("Automatisk återställning");
    Console.WriteLine("------------------------");
    Console.WriteLine("Den här versionen av KidShell kan inte ändra Windows, så den kan inte heller");
    Console.WriteLine("ångra åt dig automatiskt. Följ stegen ovan för hand.");
    Console.WriteLine();
    Console.WriteLine("Det är avsiktligt: ingen KidShell-version som har byggts hittills kan ändra");
    Console.WriteLine("den här datorn, och då vore det oärligt att låtsas kunna ångra en ändring.");
    return 0;
}

return 0;

static string? ValueAfter(string[] arguments, string flag)
{
    var index = Array.IndexOf(arguments, flag);

    return index >= 0 && index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
        ? arguments[index + 1]
        : null;
}

/// <summary>Where KidShell writes recovery manifests.</summary>
public static class RecoveryPaths
{
    /// <summary>
    /// The machine-wide location, readable by an administrator who is not the
    /// account KidShell ran under - which is the whole point on a bad day.
    /// </summary>
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidShell", "security", "recovery");
}

/// <summary>Checks the tool's formatting without reading the machine.</summary>
public static class RecoverySelfTest
{
    public static bool Run()
    {
        var failures = new List<string>();

        var manifest = new RecoveryManifest
        {
            TransactionId = "selftest",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Machine = new RecoveryMachineSummary
            {
                WindowsEdition = "Windows 11 Home",
                BuildNumber = 26200,
                MachineName = "TEST",
                RecoveryAdministrator = "Jimmy"
            },
            Steps =
            [
                new RecoveryStep
                {
                    OperationId = "child-account-create",
                    Description = "Barnkontot skapades",
                    ExistedBefore = false,
                    ManualRollbackHint = "Ta bort kontot i Inställningar > Konton."
                }
            ],
            FinalState = TransactionState.RollbackFailed
        };

        var text = RecoveryReport.Describe(manifest);

        if (!text.Contains("Jimmy", StringComparison.Ordinal))
        {
            failures.Add("the report should name the recovery administrator");
        }

        if (!text.Contains("VIKTIGT", StringComparison.Ordinal))
        {
            failures.Add("a failed rollback should be called out prominently");
        }

        if (text.Contains("0x", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("the report should contain no error codes");
        }

        if (!RecoveryReport.NeedsAttention(manifest))
        {
            failures.Add("a failed rollback needs attention");
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"FAIL: {failure}");
        }

        if (failures.Count > 0)
        {
            return false;
        }

        Console.WriteLine("KidShell.Recovery self-test passed. Nothing was changed.");
        return true;
    }
}

/// <summary>Writes warnings and errors to stderr; the report goes to stdout.</summary>
internal sealed class ConsoleLogger : IKidShellLogger
{
    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level >= LogLevel.Warning)
        {
            Console.Error.WriteLine($"{level}: {message}");
        }
    }
}
