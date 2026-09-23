using System.Reflection;

namespace KidShell.Core.Runtime;

/// <summary>
/// What this build is, read from the assembly rather than hard-coded.
///
/// The version comes from <c>Directory.Build.props</c> and reaches here via
/// the assembly attributes the compiler writes, so there is exactly one place
/// to change it. A constant duplicated in an About screen is a constant that
/// eventually lies.
/// </summary>
public static class BuildInfo
{
    private static readonly Lazy<string> InformationalVersion = new(() =>
    {
        var assembly = typeof(BuildInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // The SDK appends the source-control hash as "1.0.0-rc.1+abc1234".
        // Useful in a log, noise in an About box.
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    });

    private static readonly Lazy<string?> CommitHash = new(() =>
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        var plus = informational.IndexOf('+');

        if (plus < 0 || plus + 1 >= informational.Length)
        {
            return null;
        }

        var hash = informational[(plus + 1)..];
        return hash.Length > 7 ? hash[..7] : hash;
    });

    /// <summary>The product version, e.g. "1.0.0-rc.1".</summary>
    public static string Version => InformationalVersion.Value;

    /// <summary>The four-part assembly version, which matches the MSIX package version.</summary>
    public static string PackageVersion =>
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";

    /// <summary>Short commit hash when the build recorded one.</summary>
    public static string? Commit => CommitHash.Value;

    /// <summary>
    /// Whether this is a prerelease. Derived from the version string rather
    /// than a separate flag, so the two cannot disagree.
    /// </summary>
    public static bool IsPrerelease => Version.Contains('-', StringComparison.Ordinal);

    /// <summary>
    /// A one-line description for the About screen and the log header.
    /// </summary>
    public static string Describe(IRuntimeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var mode = environment.IsDevelopment ? "UTVECKLING" : "Release";
        var commit = Commit is null ? string.Empty : $" ({Commit})";

        return $"KidShell {Version} · {mode}{commit}";
    }
}
