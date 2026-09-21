using KidShell.Core.Configuration;

namespace KidShell.Core.Launching;

/// <summary>
/// The only way KidShell starts another program. Views and view models never
/// touch Process.Start directly.
/// </summary>
public interface IAppLauncher
{
    LaunchResult Launch(KidAppDefinition app);
}

/// <summary>
/// Decides whether a configured command can actually be started on this
/// machine, without starting anything. Split out from the launcher so that
/// resolution rules are testable with a fake.
/// </summary>
public interface IExecutableResolver
{
    ExecutableResolution Resolve(string executablePath);
}

public enum ExecutableResolutionKind
{
    /// <summary>Nothing configured.</summary>
    Empty = 0,

    /// <summary>A real file on disk.</summary>
    File = 1,

    /// <summary>A shell/protocol activation such as "calculator:" or "ms-paint:".</summary>
    ShellTarget = 2,

    /// <summary>Configured, but nothing matching was found.</summary>
    NotFound = 3
}

public sealed record ExecutableResolution(ExecutableResolutionKind Kind, string? ResolvedPath = null, string? Detail = null);

/// <summary>Starts a resolved command. The real implementation wraps Process.Start.</summary>
public interface IProcessRunner
{
    /// <summary>Starts the command. Throws on failure; the launcher maps that to <see cref="LaunchStatus.Failed"/>.</summary>
    void Start(string fileName, string arguments, bool useShellExecute);
}
