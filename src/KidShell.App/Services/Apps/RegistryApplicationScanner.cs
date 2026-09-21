using KidShell.Core.Apps;
using KidShell.Core.Diagnostics;
using Microsoft.Win32;

namespace KidShell.App.Services.Apps;

/// <summary>
/// Reads the two registry locations Windows uses to describe installed
/// programs: the uninstall lists and App Paths.
///
/// STRICTLY READ-ONLY: every key is opened with <c>writable: false</c>.
///
/// The uninstall list is where publisher and version come from, which the
/// Start Menu cannot supply. App Paths is how Windows resolves bare names like
/// <c>mspaint</c>, so it is the authority for where those actually live on
/// this machine rather than where they are assumed to live.
/// </summary>
public sealed class RegistryApplicationScanner : IApplicationScanner
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private readonly IKidShellLogger _logger;

    public RegistryApplicationScanner(IKidShellLogger logger) => _logger = logger;

    public DiscoverySource Source => DiscoverySource.RegistryUninstall;

    public Task<IReadOnlyList<DiscoveredApplication>> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<DiscoveredApplication>>([]);
        }

        var results = new List<DiscoveredApplication>();

        // 64-bit and 32-bit views, machine and user hives.
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default)
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectUninstallEntries(hive, view, results);
            CollectAppPaths(hive, view, results);
        }

        _logger.Info("Apps", $"Registry scan found {results.Count} entries.");
        return Task.FromResult<IReadOnlyList<DiscoveredApplication>>(results);
    }

    private void CollectUninstallEntries(RegistryHive hive, RegistryView view, List<DiscoveredApplication> results)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey, writable: false);

            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName, writable: false);

                if (entry is null || Describe(entry) is not { } app)
                {
                    continue;
                }

                results.Add(app);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Apps", $"Could not read the uninstall list ({hive}/{view}).", ex);
        }
    }

    private static DiscoveredApplication? Describe(RegistryKey entry)
    {
        var name = entry.GetValue("DisplayName") as string;

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Updates and system components are not programs a child launches.
        if (entry.GetValue("SystemComponent") is int component && component != 0)
        {
            return null;
        }

        if (entry.GetValue("ParentKeyName") is string parent && parent.Length > 0)
        {
            return null;
        }

        var icon = (entry.GetValue("DisplayIcon") as string ?? string.Empty).Trim().Trim('"');

        // DisplayIcon is frequently "path\app.exe,0"; the path before the
        // comma is usually the executable itself, which is the most reliable
        // launch target an uninstall entry offers.
        var comma = icon.LastIndexOf(',');
        var iconPath = comma > 2 ? icon[..comma] : icon;

        var executable = iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? iconPath
            : string.Empty;

        return new DiscoveredApplication
        {
            Key = string.Empty,
            DisplayName = name.Trim(),
            Kind = ApplicationKind.Win32,
            Source = DiscoverySource.RegistryUninstall,
            Publisher = (entry.GetValue("Publisher") as string ?? string.Empty).Trim(),
            Version = (entry.GetValue("DisplayVersion") as string ?? string.Empty).Trim(),
            ExecutablePath = executable,
            IconPath = iconPath,
            TargetExists = executable.Length > 0 && File.Exists(executable)
        };
    }

    /// <summary>
    /// App Paths: the authority for where a bare command name resolves on THIS
    /// machine. This is how KidShell learns the real location of mspaint.exe
    /// rather than assuming one.
    /// </summary>
    private void CollectAppPaths(RegistryHive hive, RegistryView view, List<DiscoveredApplication> results)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = baseKey.OpenSubKey(AppPathsKey, writable: false);

            if (appPaths is null)
            {
                return;
            }

            foreach (var subKeyName in appPaths.GetSubKeyNames())
            {
                if (!subKeyName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var entry = appPaths.OpenSubKey(subKeyName, writable: false);

                var target = (entry?.GetValue(null) as string ?? string.Empty).Trim().Trim('"');

                if (target.Length == 0 || !File.Exists(target))
                {
                    continue;
                }

                results.Add(new DiscoveredApplication
                {
                    Key = string.Empty,
                    DisplayName = Path.GetFileNameWithoutExtension(subKeyName),
                    Kind = ApplicationKind.Win32,
                    Source = DiscoverySource.AppPaths,
                    ExecutablePath = target,
                    IconPath = target,
                    TargetExists = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Apps", $"Could not read App Paths ({hive}/{view}).", ex);
        }
    }
}
