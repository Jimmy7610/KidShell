namespace KidShell.Core.Security.Readiness;

/// <summary>
/// The result of one read-only readiness scan.
///
/// A report is a snapshot of what was observed and what would happen — it
/// carries no way to act on itself.
/// </summary>
public sealed record SecurityReadinessReport
{
    public required ReadinessState OverallState { get; init; }

    /// <summary>The best mode this machine could reach.</summary>
    public required SecurityMode RecommendedMode { get; init; }

    /// <summary>
    /// What is actually in force right now. Always
    /// <see cref="SecurityMode.Development"/> in 0.1.x, because nothing has
    /// been applied — and it must stay that way until a later milestone both
    /// applies and verifies real restrictions.
    /// </summary>
    public required SecurityMode CurrentMode { get; init; }

    public required WindowsSecurityCapabilities Capabilities { get; init; }

    public IReadOnlyList<ReadinessCheck> Checks { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<string> Blockers { get; init; } = [];

    public IReadOnlyList<PlannedAction> PlannedActions { get; init; } = [];

    public IReadOnlyList<WindowsAccount> DiscoveredAccounts { get; init; } = [];

    /// <summary>
    /// The mode the scan ran in. Always
    /// <see cref="SecurityExecutionMode.AuditOnly"/>; recorded on the report so
    /// the UI and the logs can state it rather than assume it.
    /// </summary>
    public required SecurityExecutionMode ExecutionMode { get; init; }

    public required DateTimeOffset ScannedAtUtc { get; init; }

    /// <summary>
    /// True only when Windows restrictions have been applied and verified.
    /// Hard-coded false for the whole 0.1.x line: there is no code that could
    /// set it, because there is no code that applies anything.
    /// </summary>
    public bool WindowsLockdownEnabled => false;

    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>An enabled administrator account other than the child's.</summary>
    public WindowsAccount? RecoveryAccount =>
        DiscoveredAccounts.FirstOrDefault(a => a.IsRecoveryCandidate);

    public IReadOnlyList<WindowsAccount> CandidateChildAccounts =>
        [.. DiscoveredAccounts.Where(a => a.IsCandidateChildAccount)];
}
