using KidShell.Core.Security.Validation;

namespace KidShell.DeviceValidation;

/// <summary>
/// Proves the validation rules behave, and touches nothing.
///
/// What CI runs, alongside the other components' self-tests. Every check here
/// is a decision over values the test supplies: no file is read, no registry
/// key is opened, no service is queried, and nothing on the machine changes.
///
/// The emphasis is the interlock, because the interlock is the thing standing
/// between this tooling and somebody's actual computer.
/// </summary>
public static class ValidationSelfTest
{
    public static bool Run()
    {
        var failures = new List<string>();

        // ------------------------------------------------- the interlock

        var goodConfig = DeviceValidationConfig.Template() with
        {
            MachineName = "KIDSHELL-TEST-01",
            DedicatedTestDevice = true,
            ParentAdminUser = "validator",
            ChildUser = "barn",
            ExpectedChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
            EvidenceDirectory = @"C:\KidShell-Validation"
        };

        var goodFacts = new InterlockFacts
        {
            MachineName = "KIDSHELL-TEST-01",
            IsElevated = true,
            MarkerFilePresent = true,
            ConfirmationPhrase = ValidationInterlock.ConfirmationPhrase,
            IsDevelopmentBuild = false
        };

        Check(failures, "a fully prepared dedicated device opens the interlock",
            ValidationInterlock.Evaluate(goodConfig, goodFacts).Allowed);

        // Each condition on its own must be enough to refuse. This is the
        // property that makes it an interlock rather than a switch.
        Check(failures, "no config refuses",
            !ValidationInterlock.Evaluate(null, goodFacts).Allowed);

        Check(failures, "DedicatedTestDevice false refuses",
            !ValidationInterlock.Evaluate(goodConfig with { DedicatedTestDevice = false }, goodFacts).Allowed);

        Check(failures, "a config for another machine refuses",
            !ValidationInterlock.Evaluate(goodConfig with { MachineName = "SOMEBODY-ELSE" }, goodFacts).Allowed);

        Check(failures, "a missing marker refuses",
            !ValidationInterlock.Evaluate(goodConfig, goodFacts with { MarkerFilePresent = false }).Allowed);

        Check(failures, "a wrong confirmation phrase refuses",
            !ValidationInterlock.Evaluate(goodConfig, goodFacts with { ConfirmationPhrase = "yes" }).Allowed);

        Check(failures, "an empty confirmation phrase refuses",
            !ValidationInterlock.Evaluate(goodConfig, goodFacts with { ConfirmationPhrase = "" }).Allowed);

        Check(failures, "no elevation refuses",
            !ValidationInterlock.Evaluate(goodConfig, goodFacts with { IsElevated = false }).Allowed);

        Check(failures, "a development build refuses",
            !ValidationInterlock.Evaluate(goodConfig, goodFacts with { IsDevelopmentBuild = true }).Allowed);

        Check(failures, "signs of a working machine refuse",
            !ValidationInterlock.Evaluate(
                goodConfig, goodFacts with { WorkingMachineSigns = ["joined to a domain"] }).Allowed);

        Check(failures, "the parent and the child cannot be the same account",
            !ValidationInterlock.Evaluate(goodConfig with { ChildUser = "validator" }, goodFacts).Allowed);

        Check(failures, "the template as shipped refuses",
            !ValidationInterlock.Evaluate(DeviceValidationConfig.Template(), goodFacts).Allowed);

        // ------------------------------------------------- report honesty

        var empty = new ValidationReport();

        Check(failures, "a report with no stages is INCOMPLETE and not PASS",
            empty.Outcome == ValidationOutcome.Incomplete);

        Check(failures, "every required stage appears even when nothing ran",
            empty.AllStages().Count == ValidationReport.RequiredStages.Length &&
            empty.AllStages().All(s => s.Status == ValidationStageStatus.NotRun));

        Check(failures, "a stage with no findings has not run",
            new ValidationStage { Name = "X" }.Status == ValidationStageStatus.NotRun);

        Check(failures, "one failure fails a stage that otherwise passed",
            new ValidationStage
            {
                Name = "X",
                Findings = [ValidationFinding.Pass("good"), ValidationFinding.Fail("bad")]
            }.Status == ValidationStageStatus.Fail);

        Check(failures, "an unprobed check leaves a stage NOT RUN rather than passing it",
            new ValidationStage
            {
                Name = "X",
                Findings = [ValidationFinding.Pass("good"), ValidationFinding.NotRun("nobody looked")]
            }.Status == ValidationStageStatus.NotRun);

        // -------------------------------------------------- redaction

        var redacted = EvidenceRedaction.Redact(
            """{"parentPin":{"hash":"abc","salt":"def"},"usedSeconds":60,"sha256":"keepme"}""");

        Check(failures, "redaction parses and replaces the secrets", redacted.Parsed);
        Check(failures, "a PIN hash does not survive redaction",
            !redacted.Json.Contains("abc", StringComparison.Ordinal));
        Check(failures, "a salt does not survive redaction",
            !redacted.Json.Contains("def", StringComparison.Ordinal));
        Check(failures, "an ordinary value does survive redaction",
            redacted.Json.Contains("60", StringComparison.Ordinal));
        Check(failures, "an existing digest is not re-digested",
            redacted.Json.Contains("keepme", StringComparison.Ordinal));

        Check(failures, "an unparseable evidence document is refused rather than published",
            !EvidenceRedaction.Redact("{ not json").Parsed);

        // ------------------------------------------------- capabilities

        var home = new PlatformFacts
        {
            EditionId = "Core", ProductName = "Windows 11 Home", Build = 26100,
            AppIdServicePresent = true, AppIdServiceStartType = "Manual"
        };

        Check(failures, "Windows Home cannot enforce AppLocker",
            AppLockerCapability.Evaluate(home).Support == CapabilitySupport.NotSupported);

        Check(failures, "Windows Home cannot do Assigned Access",
            AssignedAccessCapability.Evaluate(home).Support == CapabilitySupport.NotSupported);

        var pro = new PlatformFacts
        {
            EditionId = "Professional", ProductName = "Windows 11 Pro", Build = 26100,
            AppIdServicePresent = true, AppIdServiceStartType = "Automatic",
            AppLockerCmdletsPresent = true, AppLockerPolicyReadable = true,
            AssignedAccessCmdletsPresent = true, MultiAppKioskAvailable = true
        };

        Check(failures, "Windows Pro with the service running supports AppLocker",
            AppLockerCapability.Evaluate(pro).Support == CapabilitySupport.Supported);

        Check(failures, "Windows Pro without the identity service is only partial",
            AppLockerCapability.Evaluate(pro with { AppIdServiceStartType = "Manual" }).Support
                == CapabilitySupport.PartiallySupported);

        // -------------------------------------------------- checkpoint

        var checkpoint = new RebootCheckpoint
        {
            RunId = "run-1", MachineName = "KIDSHELL-TEST-01", Stage = "SCREEN TIME",
            WrittenUtc = DateTimeOffset.UnixEpoch
        };

        Check(failures, "a checkpoint round-trips",
            RebootCheckpoint.Load(checkpoint.ToJson(), "KIDSHELL-TEST-01", "run-1").CanResume);

        Check(failures, "a checkpoint from another machine is not resumed",
            !RebootCheckpoint.Load(checkpoint.ToJson(), "OTHER", "run-1").CanResume);

        Check(failures, "a checkpoint from another run is not resumed",
            !RebootCheckpoint.Load(checkpoint.ToJson(), "KIDSHELL-TEST-01", "run-2").CanResume);

        Check(failures, "a checkpoint that does not parse is not resumed",
            !RebootCheckpoint.Load("{ not json", "KIDSHELL-TEST-01", "run-1").CanResume);

        // ------------------------------------------------------ verdict

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"FAIL: {failure}");
            }

            return false;
        }

        Console.WriteLine("KidShell.DeviceValidation self-test passed. Nothing was changed.");
        return true;
    }

    private static void Check(List<string> failures, string what, bool condition)
    {
        if (!condition)
        {
            failures.Add(what);
        }
    }
}
