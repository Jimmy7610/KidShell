namespace KidShell.Core.Security.Readiness;

/// <summary>
/// How disruptive a planned change is if it goes wrong.
///
/// This classifies technical change-risk — how hard it is to undo and how much
/// of the machine it touches. It is not a rating of how secure the result is.
/// </summary>
public enum ChangeRiskLevel
{
    /// <summary>Confined to KidShell's own data. Trivially reversible.</summary>
    Low = 0,

    /// <summary>Changes Windows state that can be reversed from the same UI.</summary>
    Medium = 1,

    /// <summary>Changes sign-in or policy state; recovery may need another account.</summary>
    High = 2
}

/// <summary>A capability a planned action depends on.</summary>
public enum RequiredCapability
{
    None = 0,
    AssignedAccess = 1,

    /// <summary>
    /// AppLocker rule enforcement. Edition-independent on Windows 10 2004+
    /// and Windows 11; separate from whether a policy can be deployed.
    /// </summary>
    AppLockerEnforcement = 2,

    LocalAccountManagement = 3,
    KidShellAppAllowlist = 4,

    /// <summary>A supported channel for installing an AppLocker policy.</summary>
    AppLockerDeployment = 5
}

/// <summary>
/// One step KidShell would take if a future milestone ran secure setup.
///
/// These are descriptions, not commands. Nothing in 0.1.x can execute one:
/// there is no code that consumes a PlannedAction other than the UI that
/// prints it.
/// </summary>
public sealed record PlannedAction
{
    public required int Order { get; init; }

    public required string Id { get; init; }

    /// <summary>Swedish, parent-facing.</summary>
    public required string Description { get; init; }

    /// <summary>Swedish, what it means in practice.</summary>
    public string Detail { get; init; } = string.Empty;

    public required bool RequiresAdmin { get; init; }

    public required RequiredCapability CapabilityRequired { get; init; }

    public required ChangeRiskLevel RiskLevel { get; init; }

    public required bool CanRollback { get; init; }

    /// <summary>
    /// Always false in this milestone, and asserted by tests. A plan is
    /// generated, shown and discarded.
    /// </summary>
    public bool WasExecuted { get; init; }
}
