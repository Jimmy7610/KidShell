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
            HasPackageIdentity = HasPackageIdentity()
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
