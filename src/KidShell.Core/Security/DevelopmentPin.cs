namespace KidShell.Core.Security;

/// <summary>
/// DEVELOPMENT ONLY.
///
/// Until a parent has chosen their own PIN, KidShell accepts this well-known
/// fallback so that MVP 0.1 is testable. It is a compiled-in constant, is
/// never written to the configuration file, and is only honoured while
/// <see cref="Configuration.DeveloperOptions.DeveloperMode"/> is true.
///
/// Shipping KidShell to real families REQUIRES removing this fallback and
/// forcing PIN setup during first-run. Tracked for the Windows integration
/// milestone.
/// </summary>
public static class DevelopmentPin
{
    public const string Value = "246810";
}
