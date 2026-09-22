using KidShell.Core.Configuration;
using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Tests;

/// <summary>
/// Fakes for the security readiness tests.
///
/// None of these touch Windows. That is the point: the whole capability
/// matrix, including editions this machine is not, is exercised from data.
/// </summary>
internal sealed class FakeSystemFactsProvider : ISystemFactsProvider
{
    private readonly WindowsSystemFacts _facts;
    private readonly Exception? _throws;

    public FakeSystemFactsProvider(WindowsSystemFacts facts) => _facts = facts;

    public FakeSystemFactsProvider(Exception throws)
    {
        _throws = throws;
        _facts = WindowsSystemFacts.Unknown();
    }

    public int ReadCount { get; private set; }

    public Task<WindowsSystemFacts> ReadAsync(CancellationToken cancellationToken = default)
    {
        ReadCount++;

        if (_throws is not null)
        {
            throw _throws;
        }

        return Task.FromResult(_facts);
    }
}

internal sealed class FakeAccountDiscovery : IWindowsAccountDiscovery
{
    private readonly IReadOnlyList<WindowsAccount> _accounts;
    private readonly Exception? _throws;

    public FakeAccountDiscovery(params WindowsAccount[] accounts) => _accounts = accounts;

    public FakeAccountDiscovery(Exception throws)
    {
        _throws = throws;
        _accounts = [];
    }

    public Task<IReadOnlyList<WindowsAccount>> ListLocalAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (_throws is not null)
        {
            throw _throws;
        }

        return Task.FromResult(_accounts);
    }
}

internal sealed class FakeDeveloperOptions : IDeveloperOptions
{
    public FakeDeveloperOptions(bool developerMode) => DeveloperMode = developerMode;

    public bool DeveloperMode { get; }

    public bool ShowDevelopmentWatermark => DeveloperMode;
}

internal static class SecurityFixtures
{
    /// <summary>
    /// Facts matching the machine this milestone was built on: Windows 11
    /// Home, build 26200, UAC on, signed in as an administrator.
    /// </summary>
    public static WindowsSystemFacts Windows11Home() => new()
    {
        EditionId = "Core",

        // Deliberately the value Windows really reports: ProductName still
        // says "Windows 10" on Windows 11.
        ProductName = "Windows 10 Home",
        DisplayVersion = "25H2",
        BuildNumber = 26200,
        UpdateBuildRevision = 9445,
        IsUacEnabled = true,

        // The normal state on a UAC machine: the token shows nothing, because
        // the Administrators SID is filtered out of it. Whether the account is
        // an administrator is settled by the account list.
        TokenShowsAdministrator = false,
        IsProcessElevated = false,
        CurrentUserName = "Jimmy",
        CurrentUserSid = AdminSid,
        HasPackageIdentity = true,

        // The real shape of a stock Home machine, as probed on the development
        // box: the enforcement engine is present and every first-party way of
        // installing a policy is not.
        AppIdentityServicePresent = true,
        AppIdentityServiceStartMode = "Manual",
        AppLockerPolicyStorePresent = true,
        AppLockerModuleAvailable = false,
        AppLockerLocalPolicyReadable = false,
        LocalSecurityPolicyUiPresent = false
    };

    /// <summary>The same machine with the AppLocker tooling installed too.</summary>
    public static WindowsSystemFacts WithAppLockerTooling(WindowsSystemFacts facts) => facts with
    {
        AppLockerModuleAvailable = true,
        AppLockerLocalPolicyReadable = true,
        LocalSecurityPolicyUiPresent = true
    };

    /// <summary>SID shared by the fixture's signed-in user and admin account.</summary>
    public const string AdminSid = "S-1-5-21-1-2-3-1001";

    public static WindowsSystemFacts Windows11Pro() => Windows11Home() with
    {
        EditionId = "Professional",
        ProductName = "Windows 10 Pro"
    };

    public static WindowsSystemFacts Windows11Enterprise() => Windows11Home() with
    {
        EditionId = "Enterprise",
        ProductName = "Windows 10 Enterprise"
    };

    public static WindowsSystemFacts Windows11Education() => Windows11Home() with
    {
        EditionId = "Education"
    };

    public static WindowsAccount Admin(string name = "Jimmy", bool enabled = true) => new()
    {
        Username = name,
        Sid = name == "Jimmy" ? AdminSid : $"S-1-5-21-1-2-3-{name.GetHashCode(StringComparison.Ordinal) & 0x7FFF}",
        IsAdministrator = true,
        IsEnabled = enabled,
        IsBuiltIn = false
    };

    public static WindowsAccount Standard(string name = "Barn", bool enabled = true) => new()
    {
        Username = name,
        Sid = $"S-1-5-21-1-2-3-{name.GetHashCode(StringComparison.Ordinal) & 0x7FFF}",
        IsAdministrator = false,
        IsEnabled = enabled,
        IsBuiltIn = false
    };

    public static WindowsAccount BuiltIn(string name = "DefaultAccount") => new()
    {
        Username = name,
        Sid = "S-1-5-21-1-2-3-503",
        IsAdministrator = false,
        IsEnabled = false,
        IsBuiltIn = true
    };

    /// <summary>A configuration that passes every KidShell-side pre-flight check.</summary>
    public static KidShellConfiguration ConfiguredChild()
    {
        var config = KidShellConfiguration.CreateDefault();
        config.Child.Name = "Lucas";
        config.Child.Age = 6;
        config.Child.AvatarId = AvatarIds.Owl;
        config.Child.IsOnboardingComplete = true;
        return config;
    }
}
