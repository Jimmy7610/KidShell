using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Security.AppControl;
using KidShell.Core.Security.Readiness;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Policy generation and AppLocker XML.
///
/// Everything here produces a string and asserts on it. No test installs a
/// policy, touches SrpV2, or starts the Application Identity service.
/// </summary>
public class AppControlPolicyTests
{
    private static KidShellConfiguration ConfigWith(params (string Id, string Name, string Path)[] apps)
    {
        var config = KidShellConfiguration.CreateDefault();
        config.Apps.Clear();

        var order = 0;

        foreach (var (id, name, path) in apps)
        {
            config.Apps.Add(new KidAppDefinition
            {
                Id = id,
                DisplayName = name,
                ProgramName = name,
                ExecutablePath = path,
                IsEnabled = true,
                SortOrder = order++
            });
        }

        return config;
    }

    private static AppControlPolicy Build(KidShellConfiguration config, string kidShellPath = @"C:\Program Files\KidShell\KidShell.exe") =>
        AppControlPolicyBuilder.Build(config, ApplicationProfileLibrary.Default, kidShellPath);

    // ------------------------------------------------ survival rules

    [Fact]
    public void Windows_and_KidShell_are_always_allowed()
    {
        // A policy that omits these is not strict, it is broken: the machine
        // has no desktop and the parent no way back in.
        var policy = Build(ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe")));

        Assert.Contains(policy.SystemRules, r => r.Value.Contains("%WINDIR%", StringComparison.Ordinal));
        Assert.Contains(policy.SystemRules, r => r.Name == "KidShell");
    }

    [Fact]
    public void A_policy_without_KidShells_own_path_says_so()
    {
        var policy = Build(ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe")), kidShellPath: "");

        Assert.Contains(policy.Warnings, w => w.Code == "kidshell-path-unknown");
    }

    [Fact]
    public void Packaged_apps_get_their_own_collection()
    {
        // Without an Appx rule no Store app runs at all, including Calculator
        // and Paint on Windows 11.
        var policy = Build(ConfigWith(("calc", "Miniräknare", @"C:\Windows\System32\calc.exe")));

        Assert.Contains(policy.Rules, r => r.Collection == RuleCollection.Appx);
    }

    // ------------------------------------------------ application rules

    [Fact]
    public void Each_enabled_app_becomes_a_rule()
    {
        var policy = Build(ConfigWith(
            ("paint", "Paint", @"C:\Windows\System32\mspaint.exe"),
            ("vlc", "VLC", @"C:\Program Files\VideoLAN\VLC\vlc.exe")));

        Assert.Contains(policy.ApplicationRules, r => r.Name == "Paint");
        Assert.Contains(policy.ApplicationRules, r => r.Name == "VLC");
    }

    [Fact]
    public void A_disabled_app_gets_no_rule()
    {
        var config = ConfigWith(("vlc", "VLC", @"C:\Program Files\VideoLAN\VLC\vlc.exe"));
        config.Apps[0].IsEnabled = false;

        var policy = Build(config);

        Assert.DoesNotContain(policy.ApplicationRules, r => r.Name == "VLC");
    }

    [Fact]
    public void An_app_with_no_program_is_skipped_quietly()
    {
        // A placeholder card is not something to write a rule for, and is not
        // an error either.
        var policy = Build(ConfigWith(("games", "Spel", "")));

        Assert.DoesNotContain(policy.ApplicationRules, r => r.Name == "Spel");
        Assert.DoesNotContain(policy.Warnings, w => w.Code == "unresolved-path");
    }

    [Fact]
    public void A_bare_command_name_cannot_become_a_path_rule()
    {
        // "calc.exe" is resolved by Windows, not by us. A rule for it would be
        // a rule for a path that does not exist.
        var policy = Build(ConfigWith(("calc", "Miniräknare", "calc.exe")));

        Assert.Contains(policy.Warnings, w => w.Code == "unresolved-path");
        Assert.DoesNotContain(policy.ApplicationRules, r => r.Value == "calc.exe");
    }

    [Fact]
    public void A_launchers_game_process_is_allowed_too()
    {
        // Allowing only MinecraftLauncher.exe would let it start and then fail
        // when it tries to run javaw.exe.
        var policy = Build(ConfigWith(
            ("minecraft", "Minecraft", @"C:\Program Files\Minecraft\MinecraftLauncher.exe")));

        Assert.Contains(policy.Rules, r => r.Value.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_rules_are_removed()
    {
        // Windows rejects a policy with duplicate rules; two configured apps
        // pointing at the same executable is an ordinary mistake.
        var policy = Build(ConfigWith(
            ("a", "Paint", @"C:\Windows\System32\mspaint.exe"),
            ("b", "Rita", @"C:\Windows\System32\mspaint.exe")));

        var paintRules = policy.Rules.Count(r =>
            r.Value.Equals(@"C:\Windows\System32\mspaint.exe", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, paintRules);
    }

    [Fact]
    public void An_empty_app_list_is_flagged()
    {
        var policy = Build(ConfigWith());

        Assert.False(policy.HasApplicationRules);
        Assert.Contains(policy.Warnings, w => w.Code == "no-application-rules");
    }

    // ------------------------------------------------ weak rules

    [Theory]
    [InlineData(@"C:\Users\Lucas\AppData\Local\Game\game.exe", true)]
    [InlineData(@"C:\Users\Lucas\Downloads\thing.exe", true)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", false)]
    [InlineData(@"C:\Windows\System32\mspaint.exe", false)]
    public void A_rule_in_a_user_writable_folder_is_weak(string path, bool expectWeak) =>
        Assert.Equal(expectWeak, AppControlPolicyBuilder.IsUserWritable(path));

    [Fact]
    public void A_weak_rule_is_emitted_but_declared()
    {
        // Dropping it would silently break an app the parent chose; emitting
        // it quietly would pretend to a protection a child could defeat by
        // copying a file into that folder.
        var policy = Build(ConfigWith(("game", "Spel", @"C:\Users\Lucas\AppData\Local\Game\game.exe")));

        Assert.Contains(policy.ApplicationRules, r => r.Name == "Spel");
        Assert.Contains(policy.WeakRules, r => r.Name == "Spel");
        Assert.Contains(policy.Warnings, w => w.Code == "weak-path-rule");
    }

    // ------------------------------------------------ XML generation

    [Fact]
    public void Generated_xml_is_valid()
    {
        var policy = Build(ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe")));
        var xml = AppLockerPolicyWriter.Write(policy);

        var result = AppLockerPolicyWriter.Validate(xml);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.True(result.RuleCount > 0);
    }

    [Fact]
    public void Generated_xml_has_the_documented_shape()
    {
        var policy = Build(ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe")));
        var xml = AppLockerPolicyWriter.Write(policy);

        Assert.Contains("<AppLockerPolicy Version=\"1\"", xml, StringComparison.Ordinal);
        Assert.Contains("<RuleCollection", xml, StringComparison.Ordinal);
        Assert.Contains("EnforcementMode=", xml, StringComparison.Ordinal);
        Assert.Contains("<FilePathRule", xml, StringComparison.Ordinal);
        Assert.Contains("UserOrGroupSid=", xml, StringComparison.Ordinal);
        Assert.Contains("Action=\"Allow\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_xml_defaults_to_audit_only()
    {
        // The first thing anyone should do with a generated policy is run it
        // in audit and read the event log - not enforce it on a child's
        // account and discover what broke.
        var policy = Build(ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe")));
        var xml = AppLockerPolicyWriter.Write(policy);

        Assert.Contains("EnforcementMode=\"AuditOnly\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("EnforcementMode=\"Enabled\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        // A random GUID per run would make every regeneration a diff and make
        // comparing two policies impossible.
        var config = ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe"));

        var first = AppLockerPolicyWriter.Write(Build(config));
        var second = AppLockerPolicyWriter.Write(Build(config));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Rule_ids_are_unique_within_a_policy()
    {
        var policy = Build(ConfigWith(
            ("paint", "Paint", @"C:\Windows\System32\mspaint.exe"),
            ("vlc", "VLC", @"C:\Program Files\VideoLAN\VLC\vlc.exe"),
            ("minecraft", "Minecraft", @"C:\Program Files\Minecraft\MinecraftLauncher.exe")));

        var result = AppLockerPolicyWriter.Validate(AppLockerPolicyWriter.Write(policy));

        // Windows rejects a policy with duplicate rule ids.
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }

    [Fact]
    public void The_target_sid_reaches_every_rule()
    {
        var policy = Build(ConfigWith(("paint", "Paint", @"C:\Windows\System32\mspaint.exe")))
            with { TargetUserSid = "S-1-5-21-1-2-3-1002" };

        var xml = AppLockerPolicyWriter.Write(policy);

        Assert.Contains("S-1-5-21-1-2-3-1002", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(AppLockerPolicyWriter.EveryoneSid, xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<Wrong/>")]
    [InlineData("<AppLockerPolicy><RuleCollection/></AppLockerPolicy>")]
    public void Invalid_policy_xml_is_rejected(string xml)
    {
        var result = AppLockerPolicyWriter.Validate(xml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Duplicate_rule_ids_are_detected_by_validation()
    {
        const string duplicated = """
        <AppLockerPolicy Version="1">
          <RuleCollection Type="Exe" EnforcementMode="AuditOnly">
            <FilePathRule Id="same" Name="A" UserOrGroupSid="S-1-1-0" Action="Allow">
              <Conditions><FilePathCondition Path="C:\a.exe" /></Conditions>
            </FilePathRule>
            <FilePathRule Id="same" Name="B" UserOrGroupSid="S-1-1-0" Action="Allow">
              <Conditions><FilePathCondition Path="C:\b.exe" /></Conditions>
            </FilePathRule>
          </RuleCollection>
        </AppLockerPolicy>
        """;

        var result = AppLockerPolicyWriter.Validate(duplicated);

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("mer än en gång", StringComparison.Ordinal));
    }

    // ------------------------------------------------ deployment channels

    [Fact]
    public void A_stock_Home_machine_has_no_deployment_channel()
    {
        // The real, awkward answer on this development machine: enforcement
        // works, and there is no supported way to install a policy.
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.True(capabilities.AppControl.CanEnforce);
        Assert.Null(DeploymentChannelPlanner.Recommend(capabilities));

        var summary = DeploymentChannelPlanner.Summarize(capabilities);
        Assert.Contains("odokumenterade", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_with_the_module_uses_PowerShell()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.WithAppLockerTooling(SecurityFixtures.Windows11Home()));

        Assert.Equal(DeploymentChannel.PowerShellModule, DeploymentChannelPlanner.Recommend(capabilities));
    }

    [Fact]
    public void A_Pro_machine_can_use_the_CSP()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());

        Assert.Equal(DeploymentChannel.ConfigurationServiceProvider, DeploymentChannelPlanner.Recommend(capabilities));
    }

    [Fact]
    public void Every_channel_reports_a_reason_when_unavailable()
    {
        var channels = DeploymentChannelPlanner.Evaluate(
            WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home()));

        Assert.All(channels.Where(c => !c.IsAvailable), c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
    }

    [Fact]
    public void There_is_no_undocumented_deployment_channel()
    {
        // Writing SrpV2 by hand would be unsupported and is deliberately not
        // expressible. If someone adds such a member, this fails.
        var names = Enum.GetNames<DeploymentChannel>();

        foreach (var forbidden in new[] { "Registry", "Direct", "Hack", "Force", "Raw" })
        {
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Nothing_implements_the_deployment_channel_interface()
    {
        var implementations = typeof(IAppControlDeploymentChannel).Assembly
            .GetTypes()
            .Where(t => typeof(IAppControlDeploymentChannel).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false })
            .ToArray();

        Assert.Empty(implementations);
    }
}
