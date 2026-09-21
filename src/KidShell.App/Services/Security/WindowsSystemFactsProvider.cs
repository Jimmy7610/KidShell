using System.Security.Principal;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using Microsoft.Win32;

namespace KidShell.App.Services.Security;

/// <summary>
/// Reads Windows facts for the readiness scan.
///
/// STRICTLY READ-ONLY. Every registry handle here is opened for read, every
/// identity call is a query. There is no Set, Create, Delete or Write anywhere
/// in this file, and there must never be: mutating Windows belongs to a later
/// milestone behind <see cref="SecurityExecutionMode.Apply"/>, which cannot
/// currently be constructed.
/// </summary>
public sealed class WindowsSystemFactsProvider : ISystemFactsProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string PoliciesSystemKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string AppIdentityServiceKey = @"SYSTEM\CurrentControlSet\Services\AppIDSvc";

    private readonly IKidShellLogger _logger;

    public WindowsSystemFactsProvider(IKidShellLogger logger) => _logger = logger;

    public Task<WindowsSystemFacts> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(WindowsSystemFacts.Unknown("Not running on Windows."));
        }

        try
        {
            return Task.FromResult(Read());
        }
        catch (Exception ex)
        {
            _logger.Error(SecurityAuditEvents.Category, "Could not read Windows system facts.", ex);
            return Task.FromResult(WindowsSystemFacts.Unknown(ex.Message));
        }
    }

    private WindowsSystemFacts Read()
    {
        var appIdentity = ReadAppIdentityService();

        using var currentVersion = Registry.LocalMachine.OpenSubKey(CurrentVersionKey, writable: false);

        // EditionID, not ProductName. On Windows 11 ProductName still reads
        // "Windows 10 ...", so anything derived from it is wrong on every
        // Windows 11 machine. The build number decides the generation.
        var editionId = currentVersion?.GetValue("EditionID") as string ?? string.Empty;
        var productName = currentVersion?.GetValue("ProductName") as string ?? string.Empty;
        var displayVersion = currentVersion?.GetValue("DisplayVersion") as string ?? string.Empty;

        var build = ParseBuild(currentVersion?.GetValue("CurrentBuild"));
        var ubr = currentVersion?.GetValue("UBR") as int? ?? 0;

        // Environment.OSVersion is the reliable build source for a packaged
        // app; the registry value is the fallback.
        if (Environment.OSVersion.Version.Build > 0)
        {
            build = Environment.OSVersion.Version.Build;
        }

        return new WindowsSystemFacts
        {
            EditionId = editionId,
            ProductName = productName,
            DisplayVersion = displayVersion,
            BuildNumber = build,
            UpdateBuildRevision = ubr,
            IsUacEnabled = ReadUacEnabled(),
            TokenShowsAdministrator = ReadTokenShowsAdministrator(),
            IsProcessElevated = ReadProcessIsElevated(),
            CurrentUserName = ReadCurrentUserName(),
            CurrentUserSid = ReadCurrentUserSid(),
            HasPackageIdentity = HasPackageIdentity(),

            // AppLocker management surface. Probed rather than inferred from
            // the edition: since KB 5024351 enforcement needs no particular
            // edition, so what actually varies is which of these is installed.
            AppLockerModuleAvailable = HasAppLockerModule(),
            AppLockerLocalPolicyReadable = CanReadLocalAppLockerPolicy(),
            AppIdentityServicePresent = appIdentity.Present,
            AppIdentityServiceStartMode = appIdentity.StartMode,
            AppLockerPolicyStorePresent = HasAppLockerPolicyStore(),
            LocalSecurityPolicyUiPresent = HasLocalSecurityPolicyUi()
        };
    }

    private static int ParseBuild(object? raw) =>
        raw is string text && int.TryParse(text, out var value) ? value : 0;

    /// <summary>Reads EnableLUA. Null when the value is missing or unreadable.</summary>
    private bool? ReadUacEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PoliciesSystemKey, writable: false);
            return key?.GetValue("EnableLUA") is int value ? value != 0 : null;
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not read UAC state.", ex);
            return null;
        }
    }

    /// <summary>
    /// Whether the process token itself carries Administrators membership.
    ///
    /// Under UAC this is false for an ordinary administrator: the filtered
    /// token's Administrators SID is deny-only, and the managed API omits it
    /// from Groups entirely. Whether the ACCOUNT is an administrator is
    /// therefore settled by the account list, not here.
    /// </summary>
    private bool ReadTokenShowsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

            return identity.Groups?.Any(g => g == administrators) == true;
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not read token groups.", ex);
            return false;
        }
    }

    /// <summary>SID of the signed-in account.</summary>
    private string ReadCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not read current user SID.", ex);
            return string.Empty;
        }
    }

    /// <summary>Whether this process currently holds administrative rights.</summary>
    private bool ReadProcessIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not read process elevation.", ex);
            return false;
        }
    }

    private string ReadCurrentUserName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var name = identity.Name ?? string.Empty;

            // DOMAIN\user -> user
            var slash = name.LastIndexOf('\\');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not read current user name.", ex);
            return string.Empty;
        }
    }

    // ------------------------------------------------ AppLocker probes
    // All read-only. Nothing here starts a service, changes a start type,
    // writes a policy, or calls Set-AppLockerPolicy. The AppLocker PowerShell
    // module is detected by looking for it on disk rather than by invoking
    // PowerShell, which keeps the probe cheap and side-effect free.

    /// <summary>
    /// Whether the AppLocker PowerShell module is installed.
    ///
    /// Checked on disk across every module root. On a stock Home machine it is
    /// absent entirely, which is precisely the distinction this milestone
    /// exists to report: the machine can enforce a policy it has no supported
    /// way to install.
    /// </summary>
    private bool HasAppLockerModule()
    {
        try
        {
            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "Modules"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsPowerShell", "Modules"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PowerShell", "Modules")
            };

            return roots
                .Where(Directory.Exists)
                .Any(root => Directory.Exists(Path.Combine(root, "AppLocker")));
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not probe for the AppLocker module.", ex);
            return false;
        }
    }

    /// <summary>
    /// Whether a local AppLocker policy could be read.
    ///
    /// Reading needs the module, so without it the answer is no. KidShell does
    /// not shell out to PowerShell to find out - invoking a shell to answer a
    /// capability question is a side effect waiting to happen.
    /// </summary>
    private bool CanReadLocalAppLockerPolicy() => HasAppLockerModule();

    /// <summary>
    /// Application Identity - the service that evaluates AppLocker rules.
    ///
    /// Read from its service registry key rather than through
    /// ServiceController, which would mean taking a package dependency to ask
    /// one question. The key is opened read-only: the start type is never
    /// changed and the service is never started.
    /// </summary>
    private (bool Present, string StartMode) ReadAppIdentityService()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AppIdentityServiceKey, writable: false);

            if (key is null)
            {
                return (false, string.Empty);
            }

            // Start: 0 boot, 1 system, 2 automatic, 3 manual, 4 disabled.
            var startMode = key.GetValue("Start") switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown"
            };

            return (true, startMode);
        }
        catch (Exception ex)
        {
            _logger.Debug(SecurityAuditEvents.Category, $"AppIDSvc not readable: {ex.GetType().Name}.");
            return (false, string.Empty);
        }
    }

    /// <summary>The local AppLocker policy store directory under System32.</summary>
    private bool HasAppLockerPolicyStore()
    {
        try
        {
            return Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "AppLocker"));
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not probe the AppLocker policy store.", ex);
            return false;
        }
    }

    /// <summary>
    /// The Local Security Policy / Group Policy consoles, which are the
    /// built-in way to author AppLocker rules. Absent on Home.
    /// </summary>
    private bool HasLocalSecurityPolicyUi()
    {
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

            return File.Exists(Path.Combine(system32, "secpol.msc")) ||
                   File.Exists(Path.Combine(system32, "gpedit.msc"));
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not probe for the policy consoles.", ex);
            return false;
        }
    }

    /// <summary>
    /// Whether the app is running with MSIX package identity. Assigned Access
    /// and autostart both need one, so the readiness report says so.
    /// </summary>
    private static bool HasPackageIdentity()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch
        {
            // Throws rather than returning null when there is no identity.
            return false;
        }
    }
}
