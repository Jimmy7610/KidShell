namespace KidShell.Core.Runtime;

/// <summary>
/// Which kind of build this is.
///
/// The distinction is decided at compile time and cannot be changed at run
/// time. That is deliberate: a production build must have no path — no
/// configuration value, no environment variable, no file on disk — that turns
/// developer affordances back on. Every one of those would be a way for a
/// child to reach Parent Mode with a PIN printed in the README.
/// </summary>
public enum KidShellRuntimeMode
{
    /// <summary>
    /// A shipped build. The development fallback PIN is refused, the debug
    /// shortcut into Parent Mode does not exist, and first-run setup cannot be
    /// completed without a real parent PIN.
    /// </summary>
    Production = 0,

    /// <summary>
    /// A developer build. Adds the fallback PIN, the debug shortcut and a
    /// visible watermark so the state is never a surprise.
    /// </summary>
    Development = 1
}

/// <summary>
/// What kind of build is running, and what that permits.
///
/// Implementations live in the App layer, where the compile-time symbol is
/// available. Core only consumes the answer, so tests can exercise both modes
/// without needing two builds.
/// </summary>
public interface IRuntimeEnvironment
{
    KidShellRuntimeMode Mode { get; }

    /// <summary>Build configuration name, for the diagnostics panel.</summary>
    string BuildConfiguration { get; }

    bool IsDevelopment => Mode == KidShellRuntimeMode.Development;

    bool IsProduction => Mode == KidShellRuntimeMode.Production;
}

/// <summary>
/// A fixed environment. Used by tests, and by the App layer once the
/// compile-time decision has been made.
/// </summary>
public sealed class RuntimeEnvironment : IRuntimeEnvironment
{
    public RuntimeEnvironment(KidShellRuntimeMode mode, string buildConfiguration)
    {
        Mode = mode;
        BuildConfiguration = buildConfiguration;
    }

    public KidShellRuntimeMode Mode { get; }

    public string BuildConfiguration { get; }

    /// <summary>
    /// The safe default. Anything that cannot determine what it is treats
    /// itself as production, so an unknown context loses developer
    /// affordances rather than gaining them.
    /// </summary>
    public static IRuntimeEnvironment Production { get; } =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    public static IRuntimeEnvironment Development { get; } =
        new RuntimeEnvironment(KidShellRuntimeMode.Development, "Debug");
}
