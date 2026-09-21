namespace KidShell.Core.Launching;

/// <summary>
/// Turns whatever a parent typed into something that can actually be started
/// on this Windows machine:
///
///  * empty                      -> Empty        ("inte konfigurerat ännu")
///  * "calculator:" / "http://"  -> ShellTarget  (protocol / app activation)
///  * "C:\...\app.exe"           -> File or NotFound
///  * "mspaint.exe"              -> probed against System32 and PATH
///
/// File existence is injectable so the rules can be unit tested without
/// depending on what happens to be installed on the build machine.
/// </summary>
public sealed class WindowsExecutableResolver : IExecutableResolver
{
    private readonly Func<string, bool> _fileExists;
    private readonly IReadOnlyList<string> _searchDirectories;
    private readonly IReadOnlyList<string> _executableExtensions;

    public WindowsExecutableResolver()
        : this(File.Exists, DefaultSearchDirectories())
    {
    }

    public WindowsExecutableResolver(
        Func<string, bool> fileExists,
        IReadOnlyList<string> searchDirectories,
        IReadOnlyList<string>? executableExtensions = null)
    {
        _fileExists = fileExists;
        _searchDirectories = searchDirectories;
        _executableExtensions = executableExtensions ?? [".exe", ".com", ".bat", ".cmd"];
    }

    public ExecutableResolution Resolve(string executablePath)
    {
        var raw = executablePath?.Trim() ?? string.Empty;

        if (raw.Length == 0)
        {
            return new ExecutableResolution(ExecutableResolutionKind.Empty);
        }

        raw = raw.Trim('"');

        if (LooksLikeShellTarget(raw))
        {
            return new ExecutableResolution(ExecutableResolutionKind.ShellTarget, raw, "Protocol or app activation.");
        }

        if (Path.IsPathRooted(raw))
        {
            return _fileExists(raw)
                ? new ExecutableResolution(ExecutableResolutionKind.File, raw)
                : new ExecutableResolution(ExecutableResolutionKind.NotFound, null, $"Not found: {raw}");
        }

        foreach (var candidate in EnumerateCandidates(raw))
        {
            if (_fileExists(candidate))
            {
                return new ExecutableResolution(ExecutableResolutionKind.File, candidate);
            }
        }

        return new ExecutableResolution(ExecutableResolutionKind.NotFound, null, $"Not found on PATH: {raw}");
    }

    private IEnumerable<string> EnumerateCandidates(string command)
    {
        var hasExtension = _executableExtensions.Any(
            ext => command.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        foreach (var directory in _searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (hasExtension)
            {
                yield return Path.Combine(directory, command);
                continue;
            }

            foreach (var extension in _executableExtensions)
            {
                yield return Path.Combine(directory, command + extension);
            }
        }
    }

    /// <summary>
    /// "calculator:", "ms-paint:", "https://…" and "shell:AppsFolder\…" are all
    /// things ShellExecute understands but that never exist as a file.
    /// </summary>
    internal static bool LooksLikeShellTarget(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 1)
        {
            // No colon at all, or "C:" style drive letters.
            return false;
        }

        var scheme = value[..separator];
        return scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.');
    }

    private static string[] DefaultSearchDirectories()
    {
        var directories = new List<string>();

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system32))
        {
            directories.Add(system32);
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            directories.Add(windows);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return [.. directories.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
