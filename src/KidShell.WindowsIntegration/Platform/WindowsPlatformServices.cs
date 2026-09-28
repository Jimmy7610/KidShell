using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;

namespace KidShell.WindowsIntegration.Platform;

/// <summary>
/// Registry access, scoped and typed.
///
/// Every key KidShell writes is a documented Microsoft setting. There is no
/// method that imports a .reg file, writes an arbitrary path from
/// configuration, or takes a hive as a string - a caller names a scope, a
/// subkey and a value, and that is the whole vocabulary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryStore : IRegistryStore
{
    private readonly IKidShellLogger _logger;

    public WindowsRegistryStore(IKidShellLogger logger) => _logger = logger;

    public Task<string?> ReadStringAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default)
    {
        using var key = OpenRead(scope, userSid, subKey);
        return Task.FromResult(key?.GetValue(valueName) as string);
    }

    public Task<int?> ReadDWordAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default)
    {
        using var key = OpenRead(scope, userSid, subKey);
        var value = key?.GetValue(valueName);
        return Task.FromResult(value is int i ? i : (int?)null);
    }

    public Task WriteStringAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName, string value,
        CancellationToken cancellationToken = default)
    {
        using var key = OpenWrite(scope, userSid, subKey);
        key.SetValue(valueName, value, RegistryValueKind.String);
        _logger.Info(SecurityAuditEvents.Category, $"Registry: set {scope}\\{subKey}\\{valueName}.");
        return Task.CompletedTask;
    }

    public Task WriteDWordAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName, int value,
        CancellationToken cancellationToken = default)
    {
        using var key = OpenWrite(scope, userSid, subKey);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
        _logger.Info(SecurityAuditEvents.Category, $"Registry: set {scope}\\{subKey}\\{valueName}.");
        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default)
    {
        using var key = Root(scope, userSid).OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        return Task.CompletedTask;
    }

    public Task<bool> KeyExistsAsync(
        RegistryScope scope, string? userSid, string subKey,
        CancellationToken cancellationToken = default)
    {
        using var key = OpenRead(scope, userSid, subKey);
        return Task.FromResult(key is not null);
    }

    public Task DeleteKeyAsync(
        RegistryScope scope, string? userSid, string subKey,
        CancellationToken cancellationToken = default)
    {
        Root(scope, userSid).DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        _logger.Warning(SecurityAuditEvents.Category, $"Registry: removed {scope}\\{subKey}.");
        return Task.CompletedTask;
    }

    private static RegistryKey Root(RegistryScope scope, string? userSid) => scope switch
    {
        RegistryScope.LocalMachine => Registry.LocalMachine,
        RegistryScope.CurrentUser => Registry.CurrentUser,

        // HKEY_USERS\<sid>. How a per-child setting is written from an
        // elevated helper without signing in as the child. The hive must
        // already be loaded, which it is once the account has signed in once;
        // the operations that use this check for that in preflight.
        RegistryScope.NamedUser => Registry.Users.OpenSubKey(
                                       userSid ?? throw new ArgumentNullException(nameof(userSid)),
                                       writable: true)
                                   ?? throw new InvalidOperationException(
                                       $"The registry hive for {userSid} is not loaded. The account must have signed in at least once."),

        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    private static RegistryKey? OpenRead(RegistryScope scope, string? userSid, string subKey) =>
        Root(scope, userSid).OpenSubKey(subKey, writable: false);

    private static RegistryKey OpenWrite(RegistryScope scope, string? userSid, string subKey) =>
        Root(scope, userSid).CreateSubKey(subKey, writable: true)
        ?? throw new InvalidOperationException($"Could not open {scope}\\{subKey} for writing.");
}

/// <summary>
/// Runs a tool with a typed argument list.
///
/// <see cref="ProcessStartInfo.ArgumentList"/>, never
/// <see cref="ProcessStartInfo.Arguments"/>: the framework quotes each element,
/// so a value containing a space or a quote cannot break out and become another
/// argument. That distinction is the difference between calling a tool and
/// evaluating a command line.
/// </summary>
public sealed class ToolRunner : IToolRunner
{
    private readonly IKidShellLogger _logger;

    public ToolRunner(IKidShellLogger logger) => _logger = logger;

    public async Task<ToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };

        _logger.Info(SecurityAuditEvents.Category, $"Running {executable} with {arguments.Count} argument(s).");

        process.Start();

        // A tool that hangs must not hang the transaction. The linked source
        // bounds it without cancelling the caller's token.
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{executable} did not finish within {timeout}.");
        }

        return new ToolResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout.ConfigureAwait(false),
            StandardError = await stderr.ConfigureAwait(false)
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the timeout and the kill.
        }
    }
}

/// <summary>
/// Service control through <c>sc.exe</c>, the documented command-line
/// interface to the service control manager.
///
/// sc.exe rather than a P/Invoke to CreateService: the arguments are a fixed,
/// validated list, the tool is present on every Windows installation, and the
/// exit codes are documented. The executable path is validated before it is
/// ever passed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScServiceControl : IServiceControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly string[] ValidStartTypes = ["auto", "demand", "disabled", "delayed-auto"];

    private readonly IToolRunner _runner;
    private readonly IKidShellLogger _logger;

    public ScServiceControl(IToolRunner runner, IKidShellLogger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    private static string ScPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");

    public async Task<ServiceStatus> QueryAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);

        var query = await _runner.RunAsync(ScPath, ["query", name], Timeout, cancellationToken).ConfigureAwait(false);

        // 1060 = the service does not exist. An expected answer, not a failure.
        if (!query.Succeeded)
        {
            return new ServiceStatus { Name = name, IsInstalled = false };
        }

        var running = query.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);

        var config = await _runner.RunAsync(ScPath, ["qc", name], Timeout, cancellationToken).ConfigureAwait(false);
        var startType = ParseStartType(config.StandardOutput);

        return new ServiceStatus
        {
            Name = name,
            IsInstalled = true,
            IsRunning = running,
            StartType = startType
        };
    }

    private static string ParseStartType(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase))
            {
                return "Automatic";
            }

            if (line.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase))
            {
                return "Manual";
            }

            if (line.Contains("DISABLED", StringComparison.OrdinalIgnoreCase))
            {
                return "Disabled";
            }
        }

        return string.Empty;
    }

    public async Task InstallAsync(
        string name, string displayName, string executablePath, string startType,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ValidateStartType(startType);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "Refusing to register a service for an executable that does not exist.", executablePath);
        }

        // binPath must be an absolute path, and sc.exe requires the "key= value"
        // spacing. The path is quoted because Program Files contains a space.
        var result = await _runner.RunAsync(ScPath,
            ["create", name, "binPath=", $"\"{Path.GetFullPath(executablePath)}\"",
             "DisplayName=", displayName, "start=", startType],
            Timeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"sc create failed ({result.ExitCode}): {result.StandardOutput.Trim()}");
        }

        _logger.Warning(SecurityAuditEvents.Category, $"Installed service '{name}'.");
    }

    public async Task UninstallAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);

        var status = await QueryAsync(name, cancellationToken).ConfigureAwait(false);

        if (!status.IsInstalled)
        {
            return;
        }

        if (status.IsRunning)
        {
            await StopAsync(name, cancellationToken).ConfigureAwait(false);
        }

        var result = await _runner.RunAsync(ScPath, ["delete", name], Timeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"sc delete failed ({result.ExitCode}): {result.StandardOutput.Trim()}");
        }

        _logger.Warning(SecurityAuditEvents.Category, $"Removed service '{name}'.");
    }

    public async Task StartAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync(ScPath, ["start", name], Timeout, cancellationToken).ConfigureAwait(false);

        // 1056 = already running.
        if (!result.Succeeded && result.ExitCode != 1056)
        {
            throw new InvalidOperationException($"sc start failed ({result.ExitCode}).");
        }
    }

    public async Task StopAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync(ScPath, ["stop", name], Timeout, cancellationToken).ConfigureAwait(false);

        // 1062 = not started.
        if (!result.Succeeded && result.ExitCode != 1062)
        {
            throw new InvalidOperationException($"sc stop failed ({result.ExitCode}).");
        }
    }

    public async Task SetStartTypeAsync(string name, string startType, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ValidateStartType(startType);

        var result = await _runner.RunAsync(ScPath, ["config", name, "start=", startType], Timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"sc config failed ({result.ExitCode}).");
        }
    }

    /// <summary>
    /// A service name is a KidShell constant, never user input - but it is
    /// validated anyway, because the day it stops being a constant is the day
    /// nobody remembers to add this.
    /// </summary>
    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException($"'{name}' is not a valid service name.", nameof(name));
        }
    }

    private static void ValidateStartType(string startType)
    {
        if (!ValidStartTypes.Contains(startType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{startType}' is not one of: {string.Join(", ", ValidStartTypes)}.", nameof(startType));
        }
    }
}

/// <summary>
/// Signs the current session out through <c>ExitWindowsEx</c>.
///
/// The app never resolves this implementation on a development build. It exists
/// so that a verified secure configuration on a dedicated device can return the
/// child to the Windows sign-in screen instead of dropping them onto an
/// unrestricted desktop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionControl : ISessionControl
{
    /// <summary>EWX_LOGOFF.</summary>
    private const uint ExitWindowsLogOff = 0x00000000;

    /// <summary>SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_OTHER | PLANNED.</summary>
    private const uint ReasonPlannedApplication = 0x00040000 | 0x00000000 | 0x80000000;

    private readonly IKidShellLogger _logger;

    public WindowsSessionControl(IKidShellLogger logger) => _logger = logger;

    public Task LogOffCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        _logger.Warning(SecurityAuditEvents.Category, "Signing the current session out at the parent's request.");

        if (!ExitWindowsEx(ExitWindowsLogOff, ReasonPlannedApplication))
        {
            throw new InvalidOperationException(
                $"ExitWindowsEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return Task.CompletedTask;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}

/// <summary>Plain file access behind an interface so operations stay testable.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(path));

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }
}
