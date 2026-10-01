using System.Globalization;
using System.Text;

namespace KidShell.Core.Security.Validation;

/// <summary>
/// What a stage concluded.
///
/// FOUR VALUES, AND NOT ONE FEWER
/// ------------------------------
/// The temptation in a validation report is three: pass, fail, and a shrug.
/// The shrug is where overclaiming lives, so it is split in two.
///
/// <see cref="NotRun"/> means nobody looked. It is not a pass and it never
/// becomes one by being green in a summary.
///
/// <see cref="NotSupported"/> means this Windows cannot do the thing. It does
/// not block an overall pass, because a product that works within an edition's
/// limits has not failed - but it has to be visible, because a parent on
/// Windows Home is relying on something weaker than the same report on Pro.
/// </summary>
public enum ValidationStageStatus
{
    NotRun = 0,
    Pass = 1,
    Fail = 2,
    NotSupported = 3
}

/// <summary>One observation inside a stage.</summary>
public sealed record ValidationFinding(ValidationStageStatus Status, string Detail, bool IsWarning = false)
{
    public static ValidationFinding Pass(string detail) => new(ValidationStageStatus.Pass, detail);

    public static ValidationFinding Fail(string detail) => new(ValidationStageStatus.Fail, detail);

    public static ValidationFinding NotRun(string detail) => new(ValidationStageStatus.NotRun, detail);

    public static ValidationFinding NotSupported(string detail) =>
        new(ValidationStageStatus.NotSupported, detail);

    /// <summary>
    /// Something worth reading that is not a verdict.
    ///
    /// Carried as a Pass so it cannot quietly fail a run, and flagged so it
    /// cannot quietly disappear from one either.
    /// </summary>
    public static ValidationFinding Warn(string detail) =>
        new(ValidationStageStatus.Pass, detail, IsWarning: true);
}

/// <summary>One named stage of a validation run.</summary>
public sealed record ValidationStage
{
    public required string Name { get; init; }

    public IReadOnlyList<ValidationFinding> Findings { get; init; } = [];

    /// <summary>
    /// The stage's verdict, derived rather than set.
    ///
    /// The order is the point: any failure fails the stage, then anything
    /// unlooked-at makes it NOT RUN, then an unsupported platform, and only a
    /// stage that actually checked something and liked it passes. A stage with
    /// no findings at all has not run, which is why that is the default.
    /// </summary>
    public ValidationStageStatus Status
    {
        get
        {
            if (Findings.Any(f => f.Status == ValidationStageStatus.Fail))
            {
                return ValidationStageStatus.Fail;
            }

            if (Findings.Any(f => f.Status == ValidationStageStatus.NotRun))
            {
                return ValidationStageStatus.NotRun;
            }

            if (Findings.Count == 0)
            {
                return ValidationStageStatus.NotRun;
            }

            if (Findings.All(f => f.Status == ValidationStageStatus.NotSupported))
            {
                return ValidationStageStatus.NotSupported;
            }

            return ValidationStageStatus.Pass;
        }
    }
}

/// <summary>Whether a whole run may be called a pass.</summary>
public enum ValidationOutcome
{
    Incomplete = 0,
    Pass = 1,
    Fail = 2
}

/// <summary>
/// The report a dedicated-device run produces.
///
/// It is assembled from stage statuses rather than written by hand, because a
/// handwritten summary of a security validation is a summary somebody will be
/// optimistic in at 23:00 on the second hour.
/// </summary>
public sealed record ValidationReport
{
    public string MachineName { get; init; } = string.Empty;

    public string WindowsEdition { get; init; } = string.Empty;

    public string WindowsBuild { get; init; } = string.Empty;

    public string RunId { get; init; } = string.Empty;

    public string GitSha { get; init; } = string.Empty;

    public IReadOnlyList<ValidationStage> Stages { get; init; } = [];

    /// <summary>
    /// The stages a dedicated-device run must account for, in report order.
    ///
    /// Fixed, so a run that never reached a stage still shows it. A report
    /// that simply omits what it did not do is how "we tested everything"
    /// gets said honestly about half a procedure.
    /// </summary>
    public static readonly string[] RequiredStages =
    [
        "SECURITYHOST SERVICE",
        "PROTECTED STORE ACL",
        "NAMED PIPE",
        "CHILD AUTHORIZATION",
        "PARENT AUTHORIZATION",
        "SCREEN TIME",
        "PIN THROTTLE",
        "SERVICE RECOVERY",
        "REBOOT PERSISTENCE",
        "APPLOCKER CAPABILITY",
        "ASSIGNED ACCESS CAPABILITY"
    ];

    /// <summary>Every required stage, with absent ones as NOT RUN.</summary>
    public IReadOnlyList<ValidationStage> AllStages() =>
    [
        .. RequiredStages.Select(name =>
            Stages.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? new ValidationStage { Name = name })
    ];

    /// <summary>
    /// The one line somebody will read.
    ///
    /// Fail beats everything. Then anything not run makes the run incomplete -
    /// deliberately not a pass, because the point of this document is to be
    /// the record of what was actually observed on a real machine.
    /// </summary>
    public ValidationOutcome Outcome
    {
        get
        {
            var statuses = AllStages().Select(s => s.Status).ToList();

            if (statuses.Contains(ValidationStageStatus.Fail))
            {
                return ValidationOutcome.Fail;
            }

            return statuses.Contains(ValidationStageStatus.NotRun)
                ? ValidationOutcome.Incomplete
                : ValidationOutcome.Pass;
        }
    }

    public string ToMarkdown()
    {
        var text = new StringBuilder();

        text.AppendLine("# KidShell Dedicated Device Validation Report");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Machine: {Or(MachineName)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Windows: {Or(WindowsEdition)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Build: {Or(WindowsBuild)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Validation run: {Or(RunId)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Git SHA: {Or(GitSha)}");
        text.AppendLine();
        text.AppendLine("## Result");
        text.AppendLine();

        foreach (var stage in AllStages())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{stage.Name}: {Label(stage.Status)}");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"OVERALL: {Label(Outcome)}");
        text.AppendLine();

        var failures = AllStages()
            .SelectMany(s => s.Findings
                .Where(f => f.Status == ValidationStageStatus.Fail)
                .Select(f => (s.Name, f.Detail)))
            .ToList();

        text.AppendLine("## Critical failures");
        text.AppendLine();

        if (failures.Count == 0)
        {
            text.AppendLine("None recorded.");
        }
        else
        {
            foreach (var (stage, detail) in failures)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- **{stage}** — {detail}");
            }
        }

        text.AppendLine();

        var warnings = AllStages()
            .SelectMany(s => s.Findings.Where(f => f.IsWarning).Select(f => (s.Name, f.Detail)))
            .ToList();

        var notRun = AllStages()
            .SelectMany(s => s.Findings
                .Where(f => f.Status == ValidationStageStatus.NotRun)
                .Select(f => (s.Name, f.Detail)))
            .ToList();

        text.AppendLine("## Warnings");
        text.AppendLine();

        if (warnings.Count == 0 && notRun.Count == 0)
        {
            text.AppendLine("None recorded.");
        }
        else
        {
            foreach (var (stage, detail) in warnings)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- **{stage}** — {detail}");
            }

            foreach (var (stage, detail) in notRun)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- **{stage}** (not run) — {detail}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Evidence");
        text.AppendLine();
        text.AppendLine("Every file in this directory. Nothing here contains a PIN, a hash, a salt, a");
        text.AppendLine("token or a protected payload; state is recorded as a digest and a decision.");
        text.AppendLine();

        foreach (var stage in AllStages())
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- {stage.Name}: {stage.Findings.Count} finding(s)");
        }

        text.AppendLine();
        text.AppendLine("## Remaining blockers");
        text.AppendLine();

        var blockers = AllStages()
            .Where(s => s.Status is ValidationStageStatus.NotRun or ValidationStageStatus.Fail)
            .Select(s => s.Name)
            .ToList();

        if (blockers.Count == 0)
        {
            text.AppendLine("None from this run.");
        }
        else
        {
            foreach (var name in blockers)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {name}");
            }
        }

        return text.ToString();
    }

    public static string Label(ValidationStageStatus status) => status switch
    {
        ValidationStageStatus.Pass => "PASS",
        ValidationStageStatus.Fail => "FAIL",
        ValidationStageStatus.NotSupported => "NOT SUPPORTED",
        _ => "NOT RUN"
    };

    public static string Label(ValidationOutcome outcome) => outcome switch
    {
        ValidationOutcome.Pass => "PASS",
        ValidationOutcome.Fail => "FAIL",
        _ => "INCOMPLETE"
    };

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "(not recorded)" : value;
}
