using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Security.AppControl;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV RETEST 2, FINDING 05 — a Store app could be added and then produced no
/// AppLocker rule.
///
/// The previous pass taught the add flow to accept packaged apps, and stopped
/// there. The publisher and package family name were never carried out of
/// discovery, so by the time the policy builder saw the approved app there was
/// nothing to write a rule from: zero individual rules and a
/// "packaged-app-without-identity" warning for an app the parent had
/// explicitly allowed.
///
/// The identity cannot be recovered later. A publisher is not derivable from a
/// display name or from an AUMID, which is exactly why dropping it was fatal
/// rather than inconvenient.
///
/// These tests follow the whole path: discovery, the approved definition,
/// persistence, reload, policy build, rule.
/// </summary>
public class PackagedIdentityEndToEndTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string CalculatorFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";

    private const string MicrosoftPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    private static DiscoveredApplication Discovered(
        string name = "Miniräknare",
        string aumid = CalculatorAumid,
        string publisher = MicrosoftPublisher) => new()
        {
            Key = name.ToLowerInvariant(),
            DisplayName = name,
            Kind = ApplicationKind.Packaged,
            Source = DiscoverySource.PackagedApp,
            Publisher = publisher,
            Aumid = aumid
        };

    /// <summary>
    /// What the add-app flow produces, built the way the view model builds it.
    ///
    /// The view model itself lives in KidShell.App, which is WinUI and cannot
    /// be referenced from a test assembly. This mirrors the fields it now
    /// carries, and <c>tools/check-composition.ps1</c> is what stops the two
    /// drifting apart.
    /// </summary>
    private static KidAppDefinition Approve(DiscoveredApplication application)
    {
        var aumid = application.LaunchTarget;
        var separator = aumid.IndexOf('!', StringComparison.Ordinal);

        return new KidAppDefinition
        {
            Id = Guid.NewGuid().ToString("n"),
            DisplayName = application.DisplayName,
            ProgramName = application.DisplayName,
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = aumid,
            Publisher = application.Publisher,
            PackageFamilyName = separator > 0 ? aumid[..separator] : string.Empty
        };
    }

    private static AppControlPolicy BuildPolicy(KidShellConfiguration configuration) =>
        AppControlPolicyBuilder.Build(
            configuration,
            ApplicationProfileLibrary.Default,
            @"C:\Program Files\KidShell\KidShell.exe",
            "S-1-5-21-0-0-0-1001");

    private static KidShellConfiguration SaveAndReload(TempDirectory dir, params KidAppDefinition[] apps)
    {
        var store = new JsonConfigurationStore(
            Path.Combine(dir.Path, "kidshell.config.json"), new RecordingLogger());

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps.Clear();
        configuration.Apps.AddRange(apps);

        Assert.True(store.Save(configuration));

        return store.Load().Configuration;
    }

    // -------------------------------------------- the whole path, at once

    [Fact]
    public void A_discovered_store_app_survives_to_an_applocker_rule()
    {
        using var dir = new TempDirectory();

        var approved = Approve(Discovered());
        var reloaded = SaveAndReload(dir, approved);

        // Persistence kept the identity.
        var app = Assert.Single(reloaded.Apps);
        Assert.Equal(MicrosoftPublisher, app.Publisher);
        Assert.Equal(CalculatorFamily, app.PackageFamilyName);
        Assert.True(app.IsSecureModeReady);

        var policy = BuildPolicy(reloaded);

        // Exactly one packaged rule, with the right identity.
        var rule = Assert.Single(policy.ApplicationRules, r => r.Collection == RuleCollection.Appx);

        Assert.Equal(RuleStrategy.Publisher, rule.Strategy);
        Assert.Equal(MicrosoftPublisher, rule.Value);
        Assert.Equal(CalculatorFamily, rule.PackageName);

        // And no complaint about the thing that used to be missing.
        Assert.DoesNotContain(policy.Warnings, w => w.Code == "packaged-app-without-identity");
    }

    [Fact]
    public void Two_store_apps_produce_two_distinct_rules()
    {
        using var dir = new TempDirectory();

        var calculator = Approve(Discovered());
        var paint = Approve(Discovered("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App"));

        var reloaded = SaveAndReload(dir, calculator, paint);
        var policy = BuildPolicy(reloaded);

        var packaged = policy.ApplicationRules
            .Where(r => r.Collection == RuleCollection.Appx)
            .ToList();

        Assert.Equal(2, packaged.Count);
        Assert.Equal(2, packaged.Select(r => r.PackageName).Distinct().Count());
    }

    [Fact]
    public void The_same_store_app_twice_produces_one_rule()
    {
        using var dir = new TempDirectory();

        var reloaded = SaveAndReload(dir, Approve(Discovered()), Approve(Discovered()));
        var policy = BuildPolicy(reloaded);

        Assert.Single(policy.ApplicationRules, r => r.Collection == RuleCollection.Appx);
    }

    [Fact]
    public void An_edit_and_save_round_trip_keeps_the_identity()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");
        var store = new JsonConfigurationStore(path, new RecordingLogger());

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps.Clear();
        configuration.Apps.Add(Approve(Discovered()));
        store.Save(configuration);

        // A parent renames the card, the way the Appar page does: draft,
        // clone, commit. A clone that dropped the identity would silently
        // disarm Secure Mode on the next save.
        var draft = store.Load().Configuration.Clone();
        draft.Apps[0].DisplayName = "Räkna";
        store.Save(draft);

        var app = Assert.Single(store.Load().Configuration.Apps);

        Assert.Equal("Räkna", app.DisplayName);
        Assert.Equal(MicrosoftPublisher, app.Publisher);
        Assert.Equal(CalculatorFamily, app.PackageFamilyName);
    }

    // ---------------------------------------------- honest readiness

    [Fact]
    public void A_store_app_without_a_publisher_is_not_secure_mode_ready()
    {
        // It may still run in Standard Mode. What it must not do is claim a
        // readiness that would promise a parent an enforcement rule which
        // cannot be written.
        var app = Approve(Discovered(publisher: string.Empty));

        Assert.False(app.IsSecureModeReady);

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps.Clear();
        configuration.Apps.Add(app);

        var policy = BuildPolicy(configuration);

        Assert.Contains(policy.Warnings, w => w.Code == "packaged-app-without-identity");
        Assert.DoesNotContain(policy.ApplicationRules, r => r.Collection == RuleCollection.Appx);
    }

    [Fact]
    public void A_wildcard_publisher_is_not_an_identity()
    {
        Assert.False(Approve(Discovered(publisher: "*")).IsSecureModeReady);
    }

    [Fact]
    public void A_win32_app_is_ready_on_its_path_alone()
    {
        var app = new KidAppDefinition
        {
            Id = "paint",
            DisplayName = "Rita",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.Win32Executable,
            ExecutablePath = @"C:\Program Files\Paint\mspaint.exe"
        };

        Assert.True(app.IsSecureModeReady);
    }

    [Fact]
    public void A_win32_app_is_unaffected_by_the_packaged_fields()
    {
        using var dir = new TempDirectory();

        var paint = new KidAppDefinition
        {
            Id = "paint",
            DisplayName = "Rita",
            ProgramName = "Paint",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.Win32Executable,
            ExecutablePath = @"C:\Program Files\Paint\mspaint.exe"
        };

        var policy = BuildPolicy(SaveAndReload(dir, paint));

        Assert.Contains(policy.ApplicationRules,
            r => r.Collection == RuleCollection.Exe && r.Strategy == RuleStrategy.Path);
    }

    [Fact]
    public void A_configuration_written_before_these_fields_existed_still_loads()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");

        // An old document: a Win32 app with no launch kind and no package
        // fields at all. It must read as exactly what it is.
        File.WriteAllText(path, """
        {
          "schemaVersion": 2,
          "apps": [
            { "id": "paint", "displayName": "Rita", "isEnabled": true,
              "executablePath": "C:\\Program Files\\Paint\\mspaint.exe" }
          ]
        }
        """);

        var app = Assert.Single(new JsonConfigurationStore(path, new RecordingLogger())
            .Load().Configuration.Apps);

        Assert.Equal(ApplicationLaunchKind.Win32Executable, app.LaunchKind);
        Assert.Equal(string.Empty, app.Publisher);
        Assert.True(app.IsSecureModeReady);
    }
}
