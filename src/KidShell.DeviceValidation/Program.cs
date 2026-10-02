using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Security.Validation;
using KidShell.DeviceValidation;

// ---------------------------------------------------------------------------
// KidShell dedicated-device validation tool.
//
// Reads JSON a validation script gathered, applies the rules in
// KidShell.Core.Security.Validation, and prints a verdict. It changes nothing:
// no registry, no service control manager, no accounts, no access lists.
//
//   interlock   decide whether a mutating stage may run at all
//   config      check a validation config's shape
//   redact      strip secrets out of an evidence document
//   expect      compare observed service or ACL facts against the plan
//   capability  report what this Windows can enforce
//   checkpoint  read a reboot checkpoint, or say why it will not be resumed
//   report      assemble the final report from the stage evidence
//   --self-test prove the rules behave, touching nothing
// ---------------------------------------------------------------------------

var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (arguments.Length == 0 || arguments.Contains("--help") || arguments.Contains("-h"))
{
    Console.WriteLine("KidShell.DeviceValidation - decides whether validation evidence is acceptable.");
    Console.WriteLine();
    Console.WriteLine("  interlock   --config <f> --facts <f>");
    Console.WriteLine("  config      --config <f>");
    Console.WriteLine("  redact      --in <f> [--out <f>]");
    Console.WriteLine("  expect      --kind service|store --observed <f> [--config <f>]");
    Console.WriteLine("  capability  --facts <f>");
    Console.WriteLine("  checkpoint  --in <f> --machine <name> --run <id>");
    Console.WriteLine("  report      --run <dir> [--out <f>]");
    Console.WriteLine();
    Console.WriteLine("  release-manifest --in <f> [--bundle <dir>]");
    Console.WriteLine("  plan-install     --manifest <f> [--receipt <f>] [--architecture x64] [--production] [--repair]");
    Console.WriteLine("  install          --manifest <f> --bundle <dir> [--program-files <d>] [--apply]");
    Console.WriteLine("  verify-install   [--receipt <f>] [--program-files <d>] [--signature-checked]");
    Console.WriteLine("  uninstall        [--receipt <f>] [--program-files <d>] [--apply]");
    Console.WriteLine();
    Console.WriteLine("  --self-test");
    Console.WriteLine();
    Console.WriteLine("Reads and prints. Changes nothing on this machine.");
    return 0;
}

if (arguments.Contains("--version"))
{
    Console.WriteLine(KidShell.Core.Runtime.BuildInfo.Version);
    return 0;
}

if (arguments.Contains("--self-test"))
{
    return ValidationSelfTest.Run() ? 0 : 1;
}

try
{
    return arguments[0] switch
    {
        "interlock" => Verbs.Interlock(Arg(arguments, "--config"), Arg(arguments, "--facts")),
        "config" => Verbs.Config(Arg(arguments, "--config")),
        "redact" => Verbs.Redact(Arg(arguments, "--in"), Arg(arguments, "--out")),
        "expect" => Verbs.Expect(
            Arg(arguments, "--kind"), Arg(arguments, "--observed"), Arg(arguments, "--config")),
        "capability" => Verbs.Capability(Arg(arguments, "--facts")),
        "checkpoint" => Verbs.Checkpoint(
            Arg(arguments, "--in"), Arg(arguments, "--machine"), Arg(arguments, "--run")),
        "report" => Verbs.Report(Arg(arguments, "--run"), Arg(arguments, "--out")),

        // Release and install. The mutating ones need --apply, and the script
        // in front of them has already passed the dedicated-device interlock.
        "release-manifest" => InstallVerbs.ReleaseManifest(
            Arg(arguments, "--in"), Arg(arguments, "--bundle")),

        "plan-install" => InstallVerbs.PlanInstall(
            Arg(arguments, "--manifest"), Arg(arguments, "--receipt"),
            Arg(arguments, "--architecture"),
            arguments.Contains("--production"), arguments.Contains("--repair")),

        "install" => InstallVerbs.Install(
            Arg(arguments, "--manifest"), Arg(arguments, "--bundle"),
            Arg(arguments, "--program-files"), Arg(arguments, "--receipt"),
            Arg(arguments, "--program-data"), arguments.Contains("--apply")),

        "verify-install" => InstallVerbs.VerifyInstall(
            Arg(arguments, "--receipt"), Arg(arguments, "--program-files"),
            Arg(arguments, "--program-data"), arguments.Contains("--signature-checked")),

        "uninstall" => InstallVerbs.Uninstall(
            Arg(arguments, "--receipt"), Arg(arguments, "--program-files"),
            Arg(arguments, "--program-data"), arguments.Contains("--apply")),

        _ => Unknown(arguments[0])
    };
}
catch (Exception ex)
{
    // A tool that reads evidence and crashes has produced no verdict, which
    // must not be mistaken for a passing one.
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    return 3;
}

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown verb '{verb}'. Run with --help.");
    return 2;
}

static string? Arg(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);

    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

namespace KidShell.DeviceValidation
{
    /// <summary>
    /// Each verb, as a function from files to an exit code.
    ///
    /// EXIT CODES ARE THE CONTRACT
    /// ---------------------------
    /// 0 means the rules accepted what they were shown. Anything else means
    /// they did not, and the scripts stop. A verb never prints a verdict and
    /// returns 0 anyway, because a script checking the exit code is the only
    /// check that cannot be misread.
    /// </summary>
    internal static class Verbs
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,

            // The gathering scripts write statuses as words - "Pass", "NotRun"
            // - because a PowerShell script building evidence should not have
            // to know an enum's numeric values. Without this converter the
            // numbers round-trip and the words do not, which means every stage
            // a script wrote would fail to load and the report would show an
            // empty run as INCOMPLETE. Honest, and for entirely the wrong
            // reason.
            Converters = { new JsonStringEnumConverter() }
        };

        internal static int Interlock(string? configPath, string? factsPath)
        {
            if (factsPath is null)
            {
                Console.Error.WriteLine("interlock needs --facts.");
                return 2;
            }

            var config = DeviceValidationConfig.Parse(ReadOrNull(configPath));
            var facts = JsonSerializer.Deserialize<InterlockFactsDto>(Read(factsPath), Options)
                        ?? throw new InvalidOperationException("The interlock facts are not readable.");

            var decision = ValidationInterlock.Evaluate(config, facts.ToFacts());

            Console.WriteLine(decision.Allowed
                ? "INTERLOCK: OPEN. This machine has been declared a dedicated KidShell test device."
                : "INTERLOCK: CLOSED. Nothing will be changed.");

            foreach (var line in decision.Explain())
            {
                Console.WriteLine($"  - {line}");
            }

            // Non-zero when closed, so a script cannot proceed by ignoring the
            // text.
            return decision.Allowed ? 0 : 1;
        }

        internal static int Config(string? configPath)
        {
            if (configPath is null)
            {
                Console.Error.WriteLine("config needs --config.");
                return 2;
            }

            var config = DeviceValidationConfig.Parse(ReadOrNull(configPath));

            if (config is null)
            {
                Console.WriteLine("CONFIG: unreadable.");
                return 1;
            }

            var problems = config.Problems();

            Console.WriteLine(problems.Count == 0 ? "CONFIG: complete." : "CONFIG: incomplete.");

            foreach (var problem in problems)
            {
                Console.WriteLine($"  - {problem}");
            }

            return problems.Count == 0 ? 0 : 1;
        }

        internal static int Redact(string? inPath, string? outPath)
        {
            if (inPath is null)
            {
                Console.Error.WriteLine("redact needs --in.");
                return 2;
            }

            var result = EvidenceRedaction.Redact(Read(inPath));

            if (!result.Parsed)
            {
                // Refused rather than written. An evidence file nobody could
                // redact is one nobody should publish.
                Console.Error.WriteLine($"REDACT: {inPath} could not be parsed, so it was not redacted.");
                return 1;
            }

            if (outPath is null)
            {
                Console.WriteLine(result.Json);
            }
            else
            {
                File.WriteAllText(outPath, result.Json);
                Console.WriteLine($"REDACT: {result.RedactedFields} field(s) replaced with a digest -> {outPath}");
            }

            return 0;
        }

        internal static int Expect(string? kind, string? observedPath, string? configPath)
        {
            if (kind is null || observedPath is null)
            {
                Console.Error.WriteLine("expect needs --kind and --observed.");
                return 2;
            }

            var config = DeviceValidationConfig.Parse(ReadOrNull(configPath));
            var findings = kind switch
            {
                "service" => SecurityHostExpectation.Compare(
                    JsonSerializer.Deserialize<SecurityHostObservation>(Read(observedPath), Options)
                    ?? throw new InvalidOperationException("The service observation is not readable."),
                    config?.KidShellInstallRoot ?? @"C:\Program Files\KidShell"),

                "store" => StoreFindings(observedPath),

                _ => throw new InvalidOperationException($"Unknown --kind '{kind}'.")
            };

            var stage = new ValidationStage
            {
                Name = kind == "service" ? "SECURITYHOST SERVICE" : "PROTECTED STORE ACL",
                Findings = findings
            };

            Console.WriteLine($"{stage.Name}: {ValidationReport.Label(stage.Status)}");

            foreach (var finding in findings)
            {
                Console.WriteLine($"  [{ValidationReport.Label(finding.Status)}] {finding.Detail}");
            }

            Console.WriteLine(JsonSerializer.Serialize(stage, Options));

            return stage.Status == ValidationStageStatus.Fail ? 1 : 0;
        }

        private static ValidationFinding[] StoreFindings(string observedPath)
        {
            var document = JsonSerializer.Deserialize<StoreObservationDto>(Read(observedPath), Options)
                           ?? throw new InvalidOperationException("The store observation is not readable.");

            return ProtectedStoreExpectation.Compare(
                KidShell.Core.Security.Storage.ProtectedStorePlan.For(document.ProgramDataPath),
                document.ToObservation(),
                document.Probes ?? []);
        }

        internal static int Capability(string? factsPath)
        {
            if (factsPath is null)
            {
                Console.Error.WriteLine("capability needs --facts.");
                return 2;
            }

            var facts = JsonSerializer.Deserialize<PlatformFacts>(Read(factsPath), Options)
                        ?? throw new InvalidOperationException("The platform facts are not readable.");

            var appLocker = AppLockerCapability.Evaluate(facts);
            var assignedAccess = AssignedAccessCapability.Evaluate(facts);

            Console.WriteLine($"APPLOCKER CAPABILITY: {appLocker.Label}");
            Console.WriteLine($"  {appLocker.Reason}");
            Console.WriteLine($"ASSIGNED ACCESS CAPABILITY: {assignedAccess.Label}");
            Console.WriteLine($"  {assignedAccess.Reason}");

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                appLocker = new { appLocker.Label, appLocker.Reason },
                assignedAccess = new { assignedAccess.Label, assignedAccess.Reason }
            }, Options));

            // A capability report is information, never a failure: an edition
            // that cannot enforce AppLocker has not failed validation.
            return 0;
        }

        internal static int Checkpoint(string? inPath, string? machine, string? runId)
        {
            if (inPath is null)
            {
                Console.Error.WriteLine("checkpoint needs --in.");
                return 2;
            }

            var load = RebootCheckpoint.Load(ReadOrNull(inPath), machine ?? string.Empty, runId ?? string.Empty);

            if (!load.CanResume)
            {
                Console.WriteLine($"CHECKPOINT: not resumed, because {load.Refusal}.");
                return 1;
            }

            Console.WriteLine($"CHECKPOINT: resume at stage '{load.Checkpoint!.Stage}' " +
                              $"(written {load.Checkpoint.WrittenUtc:u}).");

            return 0;
        }

        internal static int Report(string? runDirectory, string? outPath)
        {
            if (runDirectory is null)
            {
                Console.Error.WriteLine("report needs --run.");
                return 2;
            }

            if (!Directory.Exists(runDirectory))
            {
                Console.Error.WriteLine($"No validation run directory at {runDirectory}.");
                return 2;
            }

            var stages = new List<ValidationStage>();

            // Every *.stage.json a stage script wrote. Absent stages are NOT
            // RUN by construction, which is the whole point of assembling the
            // report from evidence rather than from a checklist.
            foreach (var file in Directory.EnumerateFiles(runDirectory, "*.stage.json").OrderBy(f => f))
            {
                var stage = JsonSerializer.Deserialize<ValidationStage>(File.ReadAllText(file), Options);

                if (stage is not null)
                {
                    stages.Add(stage);
                }
            }

            var meta = JsonSerializer.Deserialize<ReportMetaDto>(
                           ReadOrNull(Path.Combine(runDirectory, "run.json")) ?? "{}", Options)
                       ?? new ReportMetaDto();

            var report = new ValidationReport
            {
                MachineName = meta.MachineName ?? string.Empty,
                WindowsEdition = meta.WindowsEdition ?? string.Empty,
                WindowsBuild = meta.WindowsBuild ?? string.Empty,
                RunId = meta.RunId ?? Path.GetFileName(runDirectory.TrimEnd('\\', '/')),
                GitSha = meta.GitSha ?? string.Empty,
                Stages = stages
            };

            var markdown = report.ToMarkdown();
            var destination = outPath ?? Path.Combine(runDirectory, "report.md");

            File.WriteAllText(destination, markdown);

            Console.WriteLine(markdown);
            Console.WriteLine($"Written to {destination}");

            // Incomplete is not a success. A run that did not reach a stage
            // must not exit zero and look finished in a build log.
            return report.Outcome == ValidationOutcome.Pass ? 0 : 1;
        }

        private static string Read(string path) =>
            File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException(path);

        private static string? ReadOrNull(string? path) =>
            path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// The shapes the scripts write.
    ///
    /// Separate from the Core records because those use required init-only
    /// properties, which a gathering script should not have to satisfy exactly.
    /// Converting here keeps the rules' types strict and the wire format
    /// forgiving.
    /// </summary>
    internal sealed record InterlockFactsDto
    {
        public string? MachineName { get; init; }

        public bool IsElevated { get; init; }

        public bool MarkerFilePresent { get; init; }

        public string? ConfirmationPhrase { get; init; }

        public bool IsDevelopmentBuild { get; init; }

        public string[]? WorkingMachineSigns { get; init; }

        internal InterlockFacts ToFacts() => new()
        {
            MachineName = MachineName ?? string.Empty,
            IsElevated = IsElevated,
            MarkerFilePresent = MarkerFilePresent,
            ConfirmationPhrase = ConfirmationPhrase ?? string.Empty,
            IsDevelopmentBuild = IsDevelopmentBuild,
            WorkingMachineSigns = WorkingMachineSigns ?? []
        };
    }

    internal sealed record StoreObservationDto
    {
        public string? Directory { get; init; }

        /// <summary>Where ProgramData is, so the plan can be built for this machine.</summary>
        public string ProgramDataPath { get; init; } = @"C:\ProgramData";

        public bool Exists { get; init; }

        public string? Owner { get; init; }

        public bool InheritanceRemoved { get; init; }

        /// <summary>Principal name to rights name, as the gathering script resolved them.</summary>
        public Dictionary<string, string>? Effective { get; init; }

        public string[]? AccessEntries { get; init; }

        public ProtectedStoreProbeOutcome[]? Probes { get; init; }

        internal ProtectedStoreObservation ToObservation() => new()
        {
            Directory = Directory ?? @"C:\ProgramData\KidShell\policy",
            Exists = Exists,
            Owner = Owner ?? string.Empty,
            InheritanceRemoved = InheritanceRemoved,
            AccessEntries = AccessEntries ?? [],
            Effective = (Effective ?? [])
                .Where(e => Enum.TryParse<KidShell.Core.Security.Storage.ProtectedStorePrincipal>(
                    e.Key, ignoreCase: true, out _))
                .ToDictionary(
                    e => Enum.Parse<KidShell.Core.Security.Storage.ProtectedStorePrincipal>(e.Key, true),
                    e => Enum.TryParse<KidShell.Core.Security.Storage.ProtectedStoreRights>(
                        e.Value, ignoreCase: true, out var rights)
                        ? rights
                        : KidShell.Core.Security.Storage.ProtectedStoreRights.None)
        };
    }

    internal sealed record ReportMetaDto
    {
        public string? MachineName { get; init; }

        public string? WindowsEdition { get; init; }

        public string? WindowsBuild { get; init; }

        public string? RunId { get; init; }

        public string? GitSha { get; init; }
    }
}
