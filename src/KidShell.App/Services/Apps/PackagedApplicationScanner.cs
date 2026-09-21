using KidShell.Core.Apps;
using KidShell.Core.Diagnostics;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace KidShell.App.Services.Apps;

/// <summary>
/// Enumerates MSIX/UWP packages installed for the current user.
///
/// STRICTLY READ-ONLY: only <c>PackageManager.FindPackagesForUser</c> is used.
/// The install, stage, register and remove methods on that class are never
/// called and must never be.
///
/// Packaged apps matter because on Windows 11 the programs a child actually
/// wants — Calculator, Paint, the Store versions of everything — are packaged.
/// They have no meaningful executable path; the AUMID is their identity, both
/// for launching and for future application control.
/// </summary>
public sealed class PackagedApplicationScanner : IApplicationScanner
{
    private readonly IKidShellLogger _logger;

    public PackagedApplicationScanner(IKidShellLogger logger) => _logger = logger;

    public DiscoverySource Source => DiscoverySource.PackagedApp;

    public Task<IReadOnlyList<DiscoveredApplication>> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<DiscoveredApplication>>([]);
        }

        var results = new List<DiscoveredApplication>();

        try
        {
            var manager = new PackageManager();

            // Current user only: enumerating every user's packages would need
            // elevation and would show a parent things that are not theirs.
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    Describe(package, results);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Apps", $"Package skipped ({ex.GetType().Name}).");
                }
            }
        }
        catch (Exception ex)
        {
            // Enumeration can fail on a machine with an unhealthy package
            // store. Degrade to "no packaged apps" rather than breaking the
            // Add app screen.
            _logger.Warning("Apps", "Could not enumerate packaged applications.", ex);
        }

        _logger.Info("Apps", $"Packaged scan found {results.Count} entries.");
        return Task.FromResult<IReadOnlyList<DiscoveredApplication>>(results);
    }

    private static void Describe(Package package, List<DiscoveredApplication> results)
    {
        // Framework packages and resource packages are dependencies, not
        // things anyone launches.
        if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
        {
            return;
        }

        // A package can publish several entry points; each is a separate
        // launchable app with its own AUMID.
        foreach (var entry in package.GetAppListEntries())
        {
            var name = entry.DisplayInfo?.DisplayName;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            results.Add(new DiscoveredApplication
            {
                Key = string.Empty,
                DisplayName = name.Trim(),
                Kind = ApplicationKind.Packaged,
                Source = DiscoverySource.PackagedApp,
                Publisher = SafePublisher(package),
                Aumid = entry.AppUserModelId,
                Version = SafeVersion(package),

                // A packaged app has no path to point at, and needs none: the
                // AUMID is what launches it.
                TargetExists = true
            });
        }
    }

    private static string SafePublisher(Package package)
    {
        try
        {
            // DisplayName is the human one; Publisher is the certificate
            // subject, which is not what a parent wants to read.
            return package.PublisherDisplayName?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeVersion(Package package)
    {
        try
        {
            var v = package.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            return string.Empty;
        }
    }
}
