namespace KidShell.Core.Apps;

/// <summary>
/// Scans the machine for installed applications. Read-only by contract:
/// implementations enumerate, they never install, register or modify
/// anything.
/// </summary>
public interface IApplicationScanner
{
    /// <summary>Which source this scanner covers, for diagnostics.</summary>
    DiscoverySource Source { get; }

    Task<IReadOnlyList<DiscoveredApplication>> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>The merged, de-duplicated view of what is installed.</summary>
public interface IApplicationCatalog
{
    /// <summary>Runs every scanner and merges the results.</summary>
    Task<IReadOnlyList<DiscoveredApplication>> GetApplicationsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>Filters the catalogue by a free-text query.</summary>
    Task<IReadOnlyList<DiscoveredApplication>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Merges the output of several scanners into one list a parent can read.
///
/// The merging is the interesting part. The same program legitimately shows up
/// as a Start Menu shortcut, an uninstall registry entry and an App Paths
/// entry, and a parent should see "Paint" once, not three times. Entries are
/// keyed by executable where there is one and by AUMID otherwise, and richer
/// records win over thinner ones.
/// </summary>
public sealed class ApplicationCatalog : IApplicationCatalog
{
    private readonly IReadOnlyList<IApplicationScanner> _scanners;
    private readonly IApplicationProfileLibrary _profiles;

    private IReadOnlyList<DiscoveredApplication>? _cache;

    public ApplicationCatalog(IEnumerable<IApplicationScanner> scanners, IApplicationProfileLibrary profiles)
    {
        _scanners = [.. scanners];
        _profiles = profiles;
    }

    public async Task<IReadOnlyList<DiscoveredApplication>> GetApplicationsAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!refresh && _cache is not null)
        {
            return _cache;
        }

        var all = new List<DiscoveredApplication>();

        foreach (var scanner in _scanners)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                all.AddRange(await scanner.ScanAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One failing scanner must not empty the list. A parent seeing
                // fewer apps is recoverable; an exception on the Add app
                // screen is not.
            }
        }

        _cache = Merge(all, _profiles);
        return _cache;
    }

    public async Task<IReadOnlyList<DiscoveredApplication>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var all = await GetApplicationsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return Filter(all, query);
    }

    /// <summary>Free-text filter over name, publisher and file name.</summary>
    public static IReadOnlyList<DiscoveredApplication> Filter(
        IReadOnlyList<DiscoveredApplication> applications,
        string? query)
    {
        var trimmed = (query ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return applications;
        }

        return
        [
            .. applications.Where(a =>
                a.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                FileName(a.ExecutablePath).Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        ];
    }

    /// <summary>
    /// De-duplicates and sorts. Public and static so the merge rules are
    /// testable without touching a machine.
    /// </summary>
    public static IReadOnlyList<DiscoveredApplication> Merge(
        IEnumerable<DiscoveredApplication> discovered,
        IApplicationProfileLibrary? profiles = null)
    {
        var byKey = new Dictionary<string, DiscoveredApplication>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in discovered)
        {
            if (string.IsNullOrWhiteSpace(app.DisplayName))
            {
                continue;
            }

            var key = BuildKey(app);
            var normalized = app with { Key = key };

            if (profiles?.Match(normalized) is { } profile)
            {
                normalized = normalized with { ProfileId = profile.Id };
            }

            if (byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = Prefer(existing, normalized);
            }
            else
            {
                byKey[key] = normalized;
            }
        }

        return
        [
            .. byKey.Values
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Identity for de-duplication: the AUMID for packaged apps, the full
    /// executable path otherwise, and the display name as a last resort so an
    /// entry with neither is not silently dropped.
    /// </summary>
    public static string BuildKey(DiscoveredApplication app)
    {
        if (app.Kind == ApplicationKind.Packaged && !string.IsNullOrWhiteSpace(app.Aumid))
        {
            return "aumid:" + app.Aumid.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            return "exe:" + NormalizePath(app.ExecutablePath);
        }

        return "name:" + app.DisplayName.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Path normalization for comparison only. Case and trailing separators
    /// are irrelevant on Windows, and quotes routinely survive from shortcut
    /// command lines.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        trimmed = trimmed.Replace('/', '\\').TrimEnd('\\');

        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch
        {
            // Not a well-formed path; compare what we were given rather than
            // throwing out an otherwise usable entry.
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Picks the better of two records for the same application.
    ///
    /// Richer wins: a record with a publisher, an icon and a confirmed target
    /// is more useful to a parent than a bare shortcut, whichever scanner
    /// happened to produce it.
    /// </summary>
    private static DiscoveredApplication Prefer(DiscoveredApplication a, DiscoveredApplication b) =>
        Score(b) > Score(a) ? Merged(b, a) : Merged(a, b);

    /// <summary>Fills gaps in the winner from the loser rather than discarding it.</summary>
    private static DiscoveredApplication Merged(DiscoveredApplication winner, DiscoveredApplication other) =>
        winner with
        {
            Publisher = Fill(winner.Publisher, other.Publisher),
            IconPath = Fill(winner.IconPath, other.IconPath),
            Version = Fill(winner.Version, other.Version),
            Arguments = Fill(winner.Arguments, other.Arguments),
            Aumid = Fill(winner.Aumid, other.Aumid),
            ExecutablePath = Fill(winner.ExecutablePath, other.ExecutablePath),
            ProfileId = winner.ProfileId ?? other.ProfileId,
            TargetExists = winner.TargetExists || other.TargetExists
        };

    private static string Fill(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static int Score(DiscoveredApplication app)
    {
        var score = 0;

        if (app.TargetExists) score += 8;
        if (!string.IsNullOrWhiteSpace(app.Publisher)) score += 4;
        if (!string.IsNullOrWhiteSpace(app.IconPath)) score += 2;
        if (!string.IsNullOrWhiteSpace(app.Version)) score += 1;

        // A known profile means KidShell understands the program, which is
        // worth more than any single field.
        if (app.ProfileId is not null) score += 16;

        return score;
    }

    private static string FileName(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        }
        catch
        {
            return string.Empty;
        }
    }
}
