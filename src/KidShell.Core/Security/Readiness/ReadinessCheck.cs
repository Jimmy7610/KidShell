namespace KidShell.Core.Security.Readiness;

public enum CheckStatus
{
    /// <summary>The check could not be evaluated on this machine.</summary>
    NotApplicable = 0,

    Passed = 1,

    /// <summary>Setup could proceed, but something is worth knowing first.</summary>
    Warning = 2,

    /// <summary>Setup for the recommended mode could not proceed.</summary>
    Failed = 3
}

/// <summary>
/// One pre-flight check.
///
/// Checks are read-only by construction: they are computed from already
/// gathered facts, configuration and account metadata, so running them cannot
/// touch Windows even by mistake.
/// </summary>
public sealed record ReadinessCheck
{
    public required string Id { get; init; }

    /// <summary>Swedish, parent-facing.</summary>
    public required string Title { get; init; }

    /// <summary>Swedish, plain language. No HRESULTs, no error codes.</summary>
    public required string Detail { get; init; }

    public required CheckStatus Status { get; init; }

    /// <summary>
    /// Whether a failure here blocks the recommended mode. A failed check that
    /// only blocks Secure on a machine that is recommending Standard is a
    /// warning, not a blocker.
    /// </summary>
    public bool BlocksRecommendedMode { get; init; }

    public bool IsPassed => Status == CheckStatus.Passed;
}

/// <summary>Overall verdict of a readiness scan.</summary>
public enum ReadinessState
{
    /// <summary>
    /// KidShell is running as a development build. Nothing about Windows has
    /// been changed and the child is not restricted. Every 0.1.x build reports
    /// this regardless of what the machine could support.
    /// </summary>
    DevelopmentOnly = 0,

    /// <summary>Something would stop secure setup from succeeding.</summary>
    NotReady = 1,

    /// <summary>Setup could run, with caveats worth reading first.</summary>
    ReadyWithWarnings = 2,

    /// <summary>Everything the recommended mode needs is in place.</summary>
    Ready = 3
}
