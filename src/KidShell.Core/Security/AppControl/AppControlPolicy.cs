namespace KidShell.Core.Security.AppControl;

/// <summary>
/// How a rule identifies what it allows.
///
/// Ordered by how hard each is to subvert. Publisher survives updates and
/// cannot be defeated by moving a file; path is the weakest, because anything
/// a child can write to that location is then allowed.
/// </summary>
public enum RuleStrategy
{
    /// <summary>Signed publisher. Survives updates; the strongest option.</summary>
    Publisher = 0,

    /// <summary>Exact file hash. Precise, but breaks on every update.</summary>
    Hash = 1,

    /// <summary>File path. Weakest - a writable path is a hole.</summary>
    Path = 2
}

/// <summary>Which kind of file a rule collection covers.</summary>
public enum RuleCollection
{
    Exe = 0,
    Msi = 1,
    Script = 2,
    Dll = 3,
    Appx = 4
}

/// <summary>One thing the child is allowed to run.</summary>
public sealed record AppControlRule
{
    public required string Id { get; init; }

    /// <summary>Parent-facing name.</summary>
    public required string Name { get; init; }

    public required RuleCollection Collection { get; init; }

    public required RuleStrategy Strategy { get; init; }

    /// <summary>Path for a path rule, publisher name for a publisher rule.</summary>
    public required string Value { get; init; }

    /// <summary>Product name for a publisher rule, where known.</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>Which KidShell app or dependency this exists for.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// True for rules KidShell requires regardless of the child's app list -
    /// Windows itself, and KidShell. Removing one breaks the machine.
    /// </summary>
    public bool IsSystemRequirement { get; init; }

    /// <summary>
    /// True when this rule's path is somewhere a standard user can write, and
    /// therefore does not actually constrain anything. Surfaced rather than
    /// silently emitted.
    /// </summary>
    public bool IsWeak { get; init; }
}

/// <summary>Why a policy could not be generated, or is not trustworthy.</summary>
public sealed record PolicyWarning(string Code, string Message);

/// <summary>
/// A complete application-control policy, independent of how it would be
/// deployed.
///
/// Default-deny: the policy lists what is allowed, and everything absent is
/// not permitted. That is the only model worth generating — an allow-list with
/// a catch-all fallback allows everything.
/// </summary>
public sealed record AppControlPolicy
{
    public required IReadOnlyList<AppControlRule> Rules { get; init; }

    public IReadOnlyList<PolicyWarning> Warnings { get; init; } = [];

    /// <summary>The account this policy is written for, when known.</summary>
    public string TargetUserSid { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public IEnumerable<AppControlRule> SystemRules => Rules.Where(r => r.IsSystemRequirement);

    public IEnumerable<AppControlRule> ApplicationRules => Rules.Where(r => !r.IsSystemRequirement);

    /// <summary>Rules whose path a standard user could write to.</summary>
    public IEnumerable<AppControlRule> WeakRules => Rules.Where(r => r.IsWeak);

    /// <summary>
    /// Whether this policy would actually constrain anything. A policy with no
    /// application rules would leave the child with Windows and KidShell only,
    /// which is valid but worth saying out loud.
    /// </summary>
    public bool HasApplicationRules => ApplicationRules.Any();
}
