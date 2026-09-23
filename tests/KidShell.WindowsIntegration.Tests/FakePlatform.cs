using KidShell.Core.Diagnostics;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>Captures log lines so tests can assert what was recorded.</summary>
internal sealed class RecordingLogger : IKidShellLogger
{
    public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];

    public void Log(LogLevel level, string category, string message, Exception? exception = null) =>
        Entries.Add((level, category, message, exception));

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);
}

/// <summary>
/// An in-memory account directory.
///
/// This is the whole reason the operations take an interface. A test can create
/// accounts, demote them, fail a deletion and assert the recovery-administrator
/// invariant, and the machine running the test never learns any of it happened.
/// </summary>
internal sealed class FakeAccountService : ILocalAccountService
{
    private readonly List<LocalAccount> _accounts;

    public FakeAccountService(params LocalAccount[] accounts) => _accounts = [.. accounts];

    /// <summary>Makes the next create throw, to exercise the failure path.</summary>
    public Exception? ThrowOnCreate { get; set; }

    /// <summary>Makes deletion silently do nothing, so rollback verification fails.</summary>
    public bool DeleteSilentlyFails { get; set; }

    public Exception? ThrowOnSetAdministrator { get; set; }

    public int CreateCount { get; private set; }

    public int DeleteCount { get; private set; }

    public int SetAdministratorCount { get; private set; }

    public IReadOnlyList<LocalAccount> Snapshot => _accounts;

    public Task<IReadOnlyList<LocalAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalAccount>>([.. _accounts]);

    public Task<LocalAccount?> FindBySidAsync(string sid, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.FirstOrDefault(a =>
            string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase)));

    public Task<LocalAccount?> FindByNameAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.FirstOrDefault(a =>
            string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)));

    public Task<LocalAccount> CreateStandardAccountAsync(
        string username, string? fullName, string? password, CancellationToken cancellationToken = default)
    {
        CreateCount++;

        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        var account = new LocalAccount
        {
            Username = username,
            Sid = $"S-1-5-21-1111111111-2222222222-3333333333-{1000 + _accounts.Count}",
            FullName = fullName ?? string.Empty,
            IsEnabled = true,
            IsAdministrator = false,
            IsBuiltIn = false
        };

        _accounts.Add(account);
        return Task.FromResult(account);
    }

    public Task DeleteAccountAsync(string sid, CancellationToken cancellationToken = default)
    {
        DeleteCount++;

        if (!DeleteSilentlyFails)
        {
            _accounts.RemoveAll(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase));
        }

        return Task.CompletedTask;
    }

    public Task SetAdministratorAsync(string sid, bool isAdministrator, CancellationToken cancellationToken = default)
    {
        SetAdministratorCount++;

        if (ThrowOnSetAdministrator is not null)
        {
            throw ThrowOnSetAdministrator;
        }

        var index = _accounts.FindIndex(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            _accounts[index] = _accounts[index] with { IsAdministrator = isAdministrator };
        }

        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(string sid, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var index = _accounts.FindIndex(a => string.Equals(a.Sid, sid, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            _accounts[index] = _accounts[index] with { IsEnabled = isEnabled };
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ fixtures

    public static LocalAccount Parent(bool enabled = true) => new()
    {
        Username = "Jimmy",
        Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        IsEnabled = enabled,
        IsAdministrator = true,
        IsBuiltIn = false
    };

    public static LocalAccount Child(bool administrator = false) => new()
    {
        Username = "Lucas",
        Sid = "S-1-5-21-1111111111-2222222222-3333333333-1002",
        IsEnabled = true,
        IsAdministrator = administrator,
        IsBuiltIn = false
    };

    public static LocalAccount BuiltInAdministrator() => new()
    {
        Username = "Administrator",
        Sid = "S-1-5-21-1111111111-2222222222-3333333333-500",
        IsEnabled = false,
        IsAdministrator = true,
        IsBuiltIn = true
    };
}

/// <summary>An in-memory registry, keyed the same way the real one is scoped.</summary>
internal sealed class FakeRegistry : IRegistryStore
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes every write throw, to exercise the apply-failure path.</summary>
    public Exception? ThrowOnWrite { get; set; }

    /// <summary>Accepts writes but does not store them, so verification fails.</summary>
    public bool WritesAreLost { get; set; }

    public IReadOnlyDictionary<string, object> Values => _values;

    public int WriteCount { get; private set; }

    public int DeleteCount { get; private set; }

    private static string Key(RegistryScope scope, string? sid, string subKey, string valueName) =>
        $"{scope}|{sid}|{subKey}|{valueName}";

    public void Seed(RegistryScope scope, string? sid, string subKey, string valueName, object value)
    {
        _values[Key(scope, sid, subKey, valueName)] = value;
        _keys.Add($"{scope}|{sid}|{subKey}");
    }

    public void SeedKey(RegistryScope scope, string? sid, string subKey) =>
        _keys.Add($"{scope}|{sid}|{subKey}");

    public Task<string?> ReadStringAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(Key(scope, userSid, subKey, valueName), out var v) ? v as string : null);

    public Task<int?> ReadDWordAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(Key(scope, userSid, subKey, valueName), out var v) && v is int i
            ? i
            : (int?)null);

    public Task WriteStringAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName, string value,
        CancellationToken cancellationToken = default)
    {
        WriteCount++;

        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        if (!WritesAreLost)
        {
            _values[Key(scope, userSid, subKey, valueName)] = value;
            _keys.Add($"{scope}|{userSid}|{subKey}");
        }

        return Task.CompletedTask;
    }

    public Task WriteDWordAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName, int value,
        CancellationToken cancellationToken = default)
    {
        WriteCount++;

        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        if (!WritesAreLost)
        {
            _values[Key(scope, userSid, subKey, valueName)] = value;
            _keys.Add($"{scope}|{userSid}|{subKey}");
        }

        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(
        RegistryScope scope, string? userSid, string subKey, string valueName,
        CancellationToken cancellationToken = default)
    {
        DeleteCount++;
        _values.Remove(Key(scope, userSid, subKey, valueName));
        return Task.CompletedTask;
    }

    public Task<bool> KeyExistsAsync(
        RegistryScope scope, string? userSid, string subKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_keys.Contains($"{scope}|{userSid}|{subKey}"));

    public Task DeleteKeyAsync(
        RegistryScope scope, string? userSid, string subKey,
        CancellationToken cancellationToken = default)
    {
        DeleteCount++;
        _keys.Remove($"{scope}|{userSid}|{subKey}");

        var prefix = $"{scope}|{userSid}|{subKey}|";

        foreach (var key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _values.Remove(key);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// A tool runner that returns canned output and records what it was asked to
/// run.
///
/// The recorded <see cref="Invocations"/> are how the tests prove arguments are
/// passed as a list rather than a command line: a value containing a space or a
/// quote arrives as one element, not several.
/// </summary>
internal sealed class FakeToolRunner : IToolRunner
{
    private readonly Queue<ToolResult> _results = new();

    public List<(string Executable, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

    public ToolResult Default { get; set; } = new() { ExitCode = 0 };

    public void Enqueue(ToolResult result) => _results.Enqueue(result);

    public void Enqueue(int exitCode, string stdout = "", string stderr = "") =>
        _results.Enqueue(new ToolResult { ExitCode = exitCode, StandardOutput = stdout, StandardError = stderr });

    public Task<ToolResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add((executable, arguments));
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Default);
    }
}

/// <summary>An in-memory service control manager.</summary>
internal sealed class FakeServiceControl : IServiceControl
{
    private readonly Dictionary<string, ServiceStatus> _services = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ThrowOnInstall { get; set; }

    public bool StartSilentlyFails { get; set; }

    public int InstallCount { get; private set; }

    public int UninstallCount { get; private set; }

    public void Seed(string name, bool running, string startType) =>
        _services[name] = new ServiceStatus
        {
            Name = name, IsInstalled = true, IsRunning = running, StartType = startType
        };

    public Task<ServiceStatus> QueryAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_services.TryGetValue(name, out var s)
            ? s
            : new ServiceStatus { Name = name, IsInstalled = false });

    public Task InstallAsync(
        string name, string displayName, string executablePath, string startType,
        CancellationToken cancellationToken = default)
    {
        InstallCount++;

        if (ThrowOnInstall is not null)
        {
            throw ThrowOnInstall;
        }

        _services[name] = new ServiceStatus
        {
            Name = name,
            IsInstalled = true,
            IsRunning = false,
            StartType = startType == "auto" ? "Automatic" : "Manual"
        };

        return Task.CompletedTask;
    }

    public Task UninstallAsync(string name, CancellationToken cancellationToken = default)
    {
        UninstallCount++;
        _services.Remove(name);
        return Task.CompletedTask;
    }

    public Task StartAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!StartSilentlyFails && _services.TryGetValue(name, out var s))
        {
            _services[name] = s with { IsRunning = true };
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_services.TryGetValue(name, out var s))
        {
            _services[name] = s with { IsRunning = false };
        }

        return Task.CompletedTask;
    }

    public Task SetStartTypeAsync(string name, string startType, CancellationToken cancellationToken = default)
    {
        if (_services.TryGetValue(name, out var s))
        {
            _services[name] = s with
            {
                StartType = startType switch
                {
                    "auto" => "Automatic",
                    "demand" => "Manual",
                    "disabled" => "Disabled",
                    _ => s.StartType
                }
            };
        }

        return Task.CompletedTask;
    }
}

/// <summary>An in-memory file system.</summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Files => _files;

    public void Seed(string path, string content) => _files[path] = content;

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.ContainsKey(path));

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.TryGetValue(path, out var c) ? c : throw new FileNotFoundException(path));

    public Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        _files[path] = content;
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        _files.Remove(path);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Records a logout instead of performing one.</summary>
internal sealed class FakeSessionControl : ISessionControl
{
    public int LogOffCount { get; private set; }

    public Task LogOffCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        LogOffCount++;
        return Task.CompletedTask;
    }
}
