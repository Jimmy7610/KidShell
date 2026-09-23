namespace KidShell.WindowsIntegration.Platform;

/// <summary>
/// One local Windows account, as the platform layer sees it.
///
/// Deliberately not the same type as <c>KidShell.Core.Security.Readiness.WindowsAccount</c>:
/// that one is a read-only discovery result shaped for the UI, this one is the
/// mutation layer's view. Keeping them apart stops a change here quietly
/// altering what the Säkerhet page believes.
/// </summary>
public sealed record LocalAccount
{
    public required string Username { get; init; }

    public required string Sid { get; init; }

    public bool IsEnabled { get; init; }

    public bool IsAdministrator { get; init; }

    public bool IsBuiltIn { get; init; }

    public string FullName { get; init; } = string.Empty;
}

/// <summary>
/// Creates, reads and adjusts local accounts.
///
/// Every mutating member is called only from an
/// <see cref="KidShell.Core.Security.Transactions.ISecurityOperation"/>, and
/// only from inside the elevated helper. The interface exists so operations
/// can be tested against a fake that mutates a dictionary.
/// </summary>
public interface ILocalAccountService
{
    Task<IReadOnlyList<LocalAccount>> ListAsync(CancellationToken cancellationToken = default);

    Task<LocalAccount?> FindBySidAsync(string sid, CancellationToken cancellationToken = default);

    Task<LocalAccount?> FindByNameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a standard local account.
    ///
    /// The password is a <see cref="System.Security.SecureString"/>-shaped
    /// responsibility of the caller: it is passed in, used, and never written
    /// to configuration, the recovery manifest or the log. Null means "no
    /// password", which Windows permits for a local account and which is the
    /// right default for a child on a device they physically hold.
    /// </summary>
    Task<LocalAccount> CreateStandardAccountAsync(
        string username,
        string? fullName,
        string? password,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an account. Used only to roll back a creation this transaction made.</summary>
    Task DeleteAccountAsync(string sid, CancellationToken cancellationToken = default);

    /// <summary>Adds or removes the account from the local Administrators group.</summary>
    Task SetAdministratorAsync(string sid, bool isAdministrator, CancellationToken cancellationToken = default);

    Task SetEnabledAsync(string sid, bool isEnabled, CancellationToken cancellationToken = default);
}

/// <summary>A registry hive KidShell is allowed to name.</summary>
public enum RegistryScope
{
    /// <summary>HKEY_LOCAL_MACHINE. Machine-wide; needs elevation.</summary>
    LocalMachine = 0,

    /// <summary>HKEY_CURRENT_USER of the process doing the writing.</summary>
    CurrentUser = 1,

    /// <summary>
    /// HKEY_USERS\&lt;child SID&gt;. How a per-child setting is written from an
    /// elevated helper without signing in as the child.
    /// </summary>
    NamedUser = 2
}

/// <summary>
/// Reads and writes registry values.
///
/// Scoped and typed rather than general: a caller names a hive, a subkey and a
/// value, and cannot express "run this .reg file". Every write site in KidShell
/// is a documented Microsoft-supported setting; there are no undocumented
/// registry tricks, and the CI source scan exists to keep it that way.
/// </summary>
public interface IRegistryStore
{
    Task<string?> ReadStringAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default);

    Task<int?> ReadDWordAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default);

    Task WriteStringAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName, string value,
        CancellationToken cancellationToken = default);

    Task WriteDWordAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName, int value,
        CancellationToken cancellationToken = default);

    Task DeleteValueAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default);

    Task<bool> KeyExistsAsync(
        RegistryScope scope, string? userSid, string subKey,
        CancellationToken cancellationToken = default);

    Task DeleteKeyAsync(
        RegistryScope scope, string? userSid, string subKey,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of running a tool.</summary>
public sealed record ToolResult
{
    public required int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a documented Windows tool with a typed argument list.
///
/// Arguments are a <c>string[]</c>, never a command line. That is the whole
/// point: a joined string is an injection surface, and "run this PowerShell
/// text" is precisely the IPC shape the privilege design forbids. Callers pass
/// the executable and its arguments separately and the process layer quotes
/// them.
/// </summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>State of a Windows service, as far as KidShell needs it.</summary>
public sealed record ServiceStatus
{
    public required string Name { get; init; }

    public required bool IsInstalled { get; init; }

    public bool IsRunning { get; init; }

    /// <summary>"Automatic", "Manual", "Disabled" or empty when not installed.</summary>
    public string StartType { get; init; } = string.Empty;
}

/// <summary>Installs, starts, stops and removes a Windows service.</summary>
public interface IServiceControl
{
    Task<ServiceStatus> QueryAsync(string name, CancellationToken cancellationToken = default);

    Task InstallAsync(
        string name, string displayName, string executablePath, string startType,
        CancellationToken cancellationToken = default);

    Task UninstallAsync(string name, CancellationToken cancellationToken = default);

    Task StartAsync(string name, CancellationToken cancellationToken = default);

    Task StopAsync(string name, CancellationToken cancellationToken = default);

    Task SetStartTypeAsync(string name, string startType, CancellationToken cancellationToken = default);
}

/// <summary>Ends a Windows session.</summary>
public interface ISessionControl
{
    /// <summary>
    /// Signs the current interactive session out, returning Windows to the
    /// sign-in screen.
    ///
    /// Never called on a development machine: the app resolves a simulated
    /// implementation unless a verified secure configuration is active.
    /// </summary>
    Task LogOffCurrentSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes files on behalf of an operation.</summary>
public interface IFileSystem
{
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
}
