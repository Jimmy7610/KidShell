using System.Xml.Linq;
using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Security.AppControl;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV FINDING 06 — the activation chain did not agree with itself.
///
/// A policy reported CanActivate = true and was then refused for missing
/// administrator recovery rules. The XML declared UTF-16 while its bytes were
/// UTF-8. Individually approved Store apps produced no rule at all. And a
/// policy allowing execution from a folder the child can write to was
/// activatable with nothing but a warning.
/// </summary>
public class AppLockerActivationTests
{
    private static KidShellConfiguration ConfigWith(params KidAppDefinition[] apps)
    {
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps.Clear();
        configuration.Apps.AddRange(apps);
        return configuration;
    }

    private static KidAppDefinition Exe(string name, string path) => new()
    {
        Id = name.ToLowerInvariant(),
        DisplayName = name,
        ProgramName = name,
        IsEnabled = true,
        LaunchKind = ApplicationLaunchKind.Win32Executable,
        ExecutablePath = path
    };

    private static AppControlPolicy Build(KidShellConfiguration? configuration = null) =>
        AppControlPolicyBuilder.Build(
            configuration ?? ConfigWith(Exe("Paint", @"C:\Program Files\Paint\mspaint.exe")),
            ApplicationProfileLibrary.Default,
            @"C:\Program Files\KidShell\KidShell.exe",
            "S-1-5-21-0-0-0-1001");

    // ------------------------------------------- 6A: one validator

    [Fact]
    public void One_validation_result_answers_for_every_caller()
    {
        var policy = Build();

        // CanActivate is the validator's answer, not a second opinion
        // computed from whatever warnings the builder happened to record.
        Assert.Equal(policy.Validation.CanEnforce, policy.CanActivate);
        Assert.Equal(policy.Validation.Blocking, policy.BlockingWarnings);
    }

    [Fact]
    public void A_policy_that_can_activate_can_also_be_written_enforcing()
    {
        // The inconsistency, stated as a test. It must be impossible for the
        // policy to say yes and the writer to say no.
        var policy = Build();

        Assert.True(policy.CanActivate);

        var xml = AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.Enabled);

        Assert.Contains("AppLockerPolicy", xml);
    }

    [Fact]
    public void A_policy_that_cannot_activate_is_refused_enforcing()
    {
        var policy = Build(ConfigWith(Exe("Spel", @"C:\Users\Lucas\AppData\Local\Spel\spel.exe")));

        Assert.False(policy.CanActivate);

        Assert.Throws<InvalidOperationException>(() =>
            AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.Enabled));
    }

    // ------------------------------------ 6B: administrator recovery

    [Fact]
    public void Every_collection_gets_an_administrator_recovery_rule()
    {
        var policy = Build();

        var childCollections = policy.Rules
            .Where(r => !r.IsRecoveryRule)
            .Select(r => r.Collection)
            .Distinct();

        foreach (var collection in childCollections)
        {
            Assert.Contains(policy.RecoveryRules, r =>
                r.Collection == collection && r.UserOrGroupSid == WellKnownSids.Administrators);
        }
    }

    [Fact]
    public void A_policy_without_a_recovery_path_is_refused()
    {
        // An application-control policy is the one change that can leave a
        // machine unable to run the tool that would undo it.
        var policy = Build() with { Rules = Build().Rules.Where(r => !r.IsRecoveryRule).ToList() };

        Assert.False(policy.CanActivate);
        Assert.Contains(policy.Validation.Blocking, w => w.Code == "missing-administrator-recovery-rule");
    }

    [Fact]
    public void The_recovery_rules_do_not_loosen_the_childs_policy()
    {
        var policy = Build();

        // Every recovery rule is scoped away from the child's account.
        Assert.All(policy.RecoveryRules, rule =>
            Assert.Equal(WellKnownSids.Administrators, rule.UserOrGroupSid));

        // And the child's own rules are untouched by their presence: none of
        // them is a blanket rule.
        Assert.DoesNotContain(policy.Rules.Where(r => !r.IsRecoveryRule),
            r => r.Value.Trim() == "*");
    }

    [Fact]
    public void The_recovery_rules_reach_the_xml_with_the_administrators_sid()
    {
        var xml = XDocument.Parse(AppLockerPolicyWriter.Write(Build()));

        var sids = xml.Descendants()
            .Where(e => e.Attribute("UserOrGroupSid") is not null)
            .Select(e => e.Attribute("UserOrGroupSid")!.Value)
            .Distinct()
            .ToList();

        Assert.Contains(WellKnownSids.Administrators, sids);
        Assert.Contains("S-1-5-21-0-0-0-1001", sids);
    }

    // --------------------------------------------- 6C: XML encoding

    [Fact]
    public void The_declaration_and_the_bytes_agree_on_utf8()
    {
        // They did not. A StringWriter IS UTF-16 and an XmlWriter takes its
        // encoding from the TextWriter rather than from XmlWriterSettings, so
        // the document announced utf-16 while every byte written was UTF-8.
        var xml = AppLockerPolicyWriter.Write(Build());

        Assert.Contains("encoding=\"utf-8\"", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utf-16", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_bytes_round_trip_with_swedish_text_intact()
    {
        var bytes = AppLockerPolicyWriter.WriteUtf8(Build());

        // No byte-order mark: the declaration states the encoding, and some
        // consumers treat a BOM as leading content.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var reparsed = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        var text = reparsed.ToString();

        // Every rule reason in this product is Swedish. A parser that believed
        // the old declaration got nonsense from the first non-ASCII character.
        Assert.Contains("ä", text, StringComparison.Ordinal);
    }

    // ------------------------------------------- 6D: packaged apps

    [Fact]
    public void An_approved_store_app_gets_its_own_publisher_rule()
    {
        // It used to get nothing. The AUMID is not a fully-qualified path, so
        // it failed the path check and was skipped: the parent approved the
        // app and the policy would have blocked it.
        var calculator = new KidAppDefinition
        {
            Id = "calc",
            DisplayName = "Miniräknare",
            ProgramName = "Miniräknare",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            PackageFamilyName = "Microsoft.WindowsCalculator_8wekyb3d8bbwe"
        };

        var policy = Build(ConfigWith(calculator));

        var rule = Assert.Single(policy.ApplicationRules, r => r.Collection == RuleCollection.Appx);

        Assert.Equal(RuleStrategy.Publisher, rule.Strategy);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", rule.PackageName);
        Assert.Contains("Microsoft Corporation", rule.Value);
        Assert.NotEqual("*", rule.Value.Trim());
    }

    [Fact]
    public void A_store_app_with_no_recorded_publisher_produces_a_warning_not_a_wildcard()
    {
        // The alternative would be publisher="*", which allows every packaged
        // app on the machine. No rule and an explanation is the honest outcome.
        var unknown = new KidAppDefinition
        {
            Id = "mystery",
            DisplayName = "Okänd app",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = "Some.Package_abc!App"
        };

        var policy = Build(ConfigWith(unknown));

        Assert.Contains(policy.Warnings, w => w.Code == "packaged-app-without-identity");
        Assert.DoesNotContain(policy.ApplicationRules,
            r => r.Strategy == RuleStrategy.Publisher && r.Value.Trim() == "*");
    }

    // ------------------------------------ 6E: child-writable paths

    [Theory]
    [InlineData(@"C:\Users\Lucas\Downloads\game.exe")]
    [InlineData(@"C:\Users\Lucas\Desktop\game.exe")]
    [InlineData(@"C:\Users\Lucas\Documents\game.exe")]
    [InlineData(@"C:\Users\Lucas\AppData\Local\Temp\game.exe")]
    [InlineData(@"C:\Users\Lucas\AppData\Roaming\game.exe")]
    [InlineData(@"%LOCALAPPDATA%\game.exe")]
    [InlineData(@"%APPDATA%\game.exe")]
    [InlineData(@"%TEMP%\game.exe")]
    [InlineData(@"%USERPROFILE%\game.exe")]
    public void An_allow_path_the_child_can_write_to_blocks_activation(string path)
    {
        // Microsoft make the same point about their own default rules: the
        // %WINDIR% path rule covers Windows\Temp, where the Users group may
        // create files, and they warn that allowing execution from there
        // "might conflict with your organization's security policy".
        var policy = Build(ConfigWith(Exe("Spel", path)));

        Assert.False(policy.CanActivate);
        Assert.Contains(policy.Validation.Blocking, w => w.Code == "child-writable-allow-path");
    }

    [Fact]
    public void A_program_files_path_is_still_fine()
    {
        var policy = Build(ConfigWith(Exe("Paint", @"C:\Program Files\Paint\mspaint.exe")));

        Assert.True(policy.CanActivate);
        Assert.DoesNotContain(policy.Validation.Blocking, w => w.Code == "child-writable-allow-path");
    }

    // ------------------------------------------ 6F: audit vs enforce

    [Fact]
    public void An_unsafe_policy_can_still_be_generated_for_audit()
    {
        // Refusing would remove the tool for diagnosing the very problem that
        // blocked enforcement. Audit blocks nothing and logs what would have
        // been refused.
        var policy = Build(ConfigWith(Exe("Spel", @"C:\Users\Lucas\Downloads\spel.exe")));

        Assert.False(policy.CanActivate);
        Assert.True(policy.Validation.CanAudit);

        var xml = AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.AuditOnly);

        Assert.Contains("AuditOnly", xml);
    }

    [Fact]
    public void An_audit_artifact_for_an_unsafe_policy_says_so_on_its_face()
    {
        var policy = Build(ConfigWith(Exe("Spel", @"C:\Users\Lucas\Downloads\spel.exe")));
        var xml = AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.AuditOnly);

        Assert.Contains("NOT SAFE TO ENFORCE", xml);
    }

    [Fact]
    public void Not_configured_is_guarded_exactly_as_enforcing_is()
    {
        // It reads like "off" and is not. Microsoft: "if enforcement isn't
        // configured and rules are present in a rule collection, those rules
        // are enforced."
        var policy = Build(ConfigWith(Exe("Spel", @"C:\Users\Lucas\Downloads\spel.exe")));

        Assert.Throws<InvalidOperationException>(() =>
            AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.NotConfigured));
    }

    [Fact]
    public void A_safe_policy_carries_no_not_safe_banner()
    {
        var xml = AppLockerPolicyWriter.Write(Build(), AppLockerPolicyWriter.EnforcementMode.AuditOnly);

        Assert.DoesNotContain("NOT SAFE TO ENFORCE", xml);
    }
}
