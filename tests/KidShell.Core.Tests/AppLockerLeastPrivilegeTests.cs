using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Security.AppControl;
using Xunit;
using KidShell.Core.Runtime;

namespace KidShell.Core.Tests;

/// <summary>
/// EXTERNAL AUDIT FINDING 06 — the generated policy allowed almost everything.
///
/// WHAT IT USED TO EMIT
/// --------------------
///     %WINDIR%\*                 every file in the Windows folder
///     %PROGRAMFILES%\*           every installed program
///     Appx publisher "*"         every packaged app, signed by anybody
///
/// The first two are the default rules from Microsoft's own AppLocker wizard,
/// and Microsoft's guidance says plainly what they are for: "a starter policy
/// when you are first testing AppLocker". The same page then names the
/// problem - %WINDIR% contains a Temp subfolder the Users group can create
/// files in - so %WINDIR%\* allows anything a child chooses to put there.
///
/// %PROGRAMFILES%\* was not a Windows requirement at all. It allowed every
/// installed program, which is precisely the set the parent was choosing
/// between. And the packaged rule allowed the Store and the browser.
///
/// A parent who turned that on would have been told their child was
/// restricted to four apps, and would have been wrong.
///
/// WHAT REPLACES IT
/// ----------------
/// An explicit manifest of the Windows components a child session actually
/// needs, one rule each, each carrying the reason it is there; named
/// publishers for Windows' own packaged shell; and the parent's chosen
/// applications. Anything that cannot be built safely blocks activation
/// rather than deploying something that does not constrain anybody.
/// </summary>
public class AppLockerLeastPrivilegeTests
{
    private static KidShellConfiguration WithApps(params (string Name, string Path)[] apps)
    {
        var config = KidShellConfiguration.CreateDefault();
        config.Apps = [];

        foreach (var (name, path) in apps)
        {
            config.Apps.Add(new KidAppDefinition
            {
                Id = name.ToLowerInvariant(),
                DisplayName = name,
                ProgramName = name,
                ExecutablePath = path,
                IsEnabled = true
            });
        }

        return config;
    }

    private static AppControlPolicy Build(KidShellConfiguration? config = null, string kidShell = @"C:\Program Files\KidShell\KidShell.exe") =>
        AppControlPolicyBuilder.Build(
            config ?? WithApps(("Paint", @"C:\Program Files\Paint\mspaint.exe")),
            ApplicationProfileLibrary.Default,
            kidShell,
            targetUserSid: "S-1-5-21-0-0-0-1001");

    // ------------------------------------------------ the three dangerous rules

    [Fact]
    public void The_whole_Windows_folder_is_not_allowed()
    {
        var policy = Build();

        Assert.DoesNotContain(policy.Rules, r => WindowsPath.Canonical(r.Value) == @"%WINDIR%\*");
    }

    [Fact]
    public void The_whole_Program_Files_folder_is_not_allowed()
    {
        var policy = Build();

        Assert.DoesNotContain(policy.Rules, r => WindowsPath.Canonical(r.Value) == @"%PROGRAMFILES%\*");
    }

    [Fact]
    public void Every_packaged_publisher_is_not_allowed()
    {
        var policy = Build();

        // The child's rules. The administrator recovery rules are deliberately
        // broad and are scoped to a different principal entirely - see
        // Recovery_rules_exist_for_administrators_only below.
        Assert.DoesNotContain(
            policy.Rules.Where(r => !r.IsRecoveryRule),
            r => r.Strategy == RuleStrategy.Publisher && r.Value.Trim() == "*");
    }

    /// <summary>
    /// The general form of the same three, so a new blanket rule anywhere is
    /// caught rather than only the three that were there.
    /// </summary>
    [Fact]
    public void No_rule_opens_a_whole_system_folder()
    {
        var policy = Build();

        var blanket = policy.Rules
            .Where(r => !r.IsRecoveryRule && AppControlPolicyBuilder.IsBlanketRule(r.Value))
            .Select(r => r.Value)
            .ToList();

        Assert.True(blanket.Count == 0, $"blanket rule(s): {string.Join(", ", blanket)}");
    }

    [Theory]
    [InlineData(@"%WINDIR%\*")]
    [InlineData(@"%PROGRAMFILES%\*")]
    [InlineData(@"%SYSTEM32%\*")]
    [InlineData(@"%OSDRIVE%\*")]
    [InlineData(@"C:\*")]
    [InlineData("*")]
    public void A_blanket_rule_is_recognised_as_one(string value) =>
        Assert.True(AppControlPolicyBuilder.IsBlanketRule(value));

    [Theory]
    [InlineData(@"%PROGRAMFILES%\Paint\*")]
    [InlineData(@"C:\Program Files\KidShell\*")]
    [InlineData(@"%SYSTEM32%\explorer.exe")]
    public void An_application_folder_is_not_a_blanket_rule(string value) =>
        Assert.False(AppControlPolicyBuilder.IsBlanketRule(value));

    // ------------------------------------------------ escape surfaces

    /// <summary>
    /// The list that matters more than the allow list.
    /// </summary>
    [Fact]
    public void No_interpreter_or_administrative_tool_is_allowed()
    {
        var policy = Build();

        foreach (var rule in policy.Rules)
        {
            var surface = EscapeSurfaces.Matching(rule.Value);

            Assert.True(surface is null,
                $"rule \"{rule.Value}\" would allow {surface?.FileName}: {surface?.Reason}");
        }
    }

    [Theory]
    [InlineData(@"%SYSTEM32%\cmd.exe")]
    [InlineData(@"%SYSTEM32%\powershell.exe")]
    [InlineData(@"C:\Windows\System32\regedit.exe")]
    [InlineData(@"%SYSTEM32%\wscript.exe")]
    [InlineData(@"%SYSTEM32%\mshta.exe")]
    [InlineData(@"%SYSTEM32%\rundll32.exe")]
    [InlineData(@"%SYSTEM32%\taskmgr.exe")]
    [InlineData(@"%SYSTEM32%\certutil.exe")]
    public void An_escape_surface_is_recognised_wherever_it_is_written(string path) =>
        Assert.NotNull(EscapeSurfaces.Matching(path));

    [Theory]
    [InlineData(@"%SYSTEM32%\explorer.exe")]
    [InlineData(@"C:\Program Files\Paint\mspaint.exe")]
    [InlineData(@"%SYSTEM32%\ctfmon.exe")]
    public void An_ordinary_program_is_not_mistaken_for_an_escape_surface(string path) =>
        Assert.Null(EscapeSurfaces.Matching(path));

    /// <summary>
    /// A parent who somehow selects a command prompt as a "child app" must not
    /// get a policy that allows it.
    /// </summary>
    [Fact]
    public void A_policy_that_would_allow_a_command_prompt_refuses_to_activate()
    {
        var policy = Build(WithApps(("Kommandotolken", @"C:\Windows\System32\cmd.exe")));

        Assert.False(policy.CanActivate);
        Assert.Contains(policy.BlockingWarnings, w => w.Code == "escape-surface-allowed");
    }

    // ------------------------------------------------ the system manifest

    [Fact]
    public void Every_system_dependency_says_why_it_is_needed()
    {
        Assert.NotEmpty(SystemDependencyManifest.Required);

        foreach (var dependency in SystemDependencyManifest.Required)
        {
            Assert.EndsWith(".exe", dependency.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.True(dependency.Reason.Length > 30,
                $"{dependency.FileName} has no real justification written against it");
        }
    }

    [Fact]
    public void No_system_dependency_is_an_escape_surface()
    {
        foreach (var dependency in SystemDependencyManifest.Required)
        {
            var surface = EscapeSurfaces.Matching(SystemDependencyManifest.PathFor(dependency));

            Assert.True(surface is null,
                $"{dependency.FileName} is on both the required list and the escape list");
        }
    }

    [Fact]
    public void The_system_rules_are_individual_files_not_folders()
    {
        var policy = Build();

        foreach (var rule in policy.SystemRules.Where(r => r.Strategy == RuleStrategy.Path))
        {
            Assert.DoesNotContain('*', rule.Value);
        }
    }

    [Fact]
    public void Windows_can_still_start_a_session()
    {
        var policy = Build();
        var values = policy.Rules.Select(r => WindowsPath.Canonical(r.Value)).ToList();

        // The two without which a signed-in session does not reach a desktop.
        Assert.Contains(@"%SYSTEM32%\USERINIT.EXE", values);
        Assert.Contains(@"%SYSTEM32%\EXPLORER.EXE", values);
    }

    [Fact]
    public void KidShell_itself_is_allowed()
    {
        var policy = Build();

        Assert.Contains(policy.Rules, r =>
            WindowsPath.FileName(r.Value).Equals("KidShell.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A policy that cannot start KidShell would lock the child out of their
    /// own machine with no way for the parent to get back in from inside the
    /// product.
    /// </summary>
    [Fact]
    public void A_policy_that_cannot_start_KidShell_says_so()
    {
        var policy = Build(kidShell: string.Empty);

        Assert.Contains(policy.Warnings, w => w.Code == "kidshell-path-unknown");
    }

    // ------------------------------------------------ packaged apps

    [Fact]
    public void Packaged_rules_name_a_publisher_rather_than_matching_all()
    {
        var policy = Build();

        var packaged = policy.Rules
            .Where(r => r.Collection == RuleCollection.Appx && !r.IsRecoveryRule)
            .ToList();

        Assert.NotEmpty(packaged);
        Assert.All(packaged, rule =>
        {
            Assert.NotEqual("*", rule.Value.Trim());
            Assert.Contains("MICROSOFT", rule.Value, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ------------------------------------------------ the parent's apps

    [Fact]
    public void Each_approved_app_gets_its_own_rule()
    {
        var policy = Build(WithApps(
            ("Paint", @"C:\Program Files\Paint\mspaint.exe"),
            ("Musik", @"C:\Program Files\Musik\musik.exe")));

        Assert.Contains(policy.ApplicationRules, r => r.Value.EndsWith("mspaint.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(policy.ApplicationRules, r => r.Value.EndsWith("musik.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_disabled_app_gets_no_rule()
    {
        var config = WithApps(("Paint", @"C:\Program Files\Paint\mspaint.exe"));
        config.Apps[0].IsEnabled = false;

        var policy = Build(config);

        Assert.DoesNotContain(policy.ApplicationRules, r => r.Value.Contains("mspaint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_app_in_a_folder_the_child_can_write_to_is_flagged()
    {
        var policy = Build(WithApps(("Spel", @"C:\Users\Lucas\AppData\Local\Spel\spel.exe")));

        // Blocking now, not advisory. A path the child can write to is not a
        // restriction: they copy anything they like into it and run it, so a
        // policy carrying one must not report that it can be enforced.
        Assert.Contains(policy.Rules, r => r.IsWeak);
        Assert.Contains(policy.Validation.Blocking, w => w.Code == "child-writable-allow-path");
        Assert.False(policy.CanActivate);
    }

    [Fact]
    public void An_app_with_only_a_command_name_produces_a_warning_not_a_rule()
    {
        var policy = Build(WithApps(("Kalkylator", "calc.exe")));

        Assert.Contains(policy.Warnings, w => w.Code == "unresolved-path");
        Assert.DoesNotContain(policy.ApplicationRules, r => r.Value.Contains("calc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_clean_policy_can_be_activated()
    {
        var policy = Build();

        Assert.True(policy.CanActivate);
        Assert.Empty(policy.BlockingWarnings);
    }

    [Fact]
    public void The_policy_is_written_for_the_child_account()
    {
        var policy = Build();

        Assert.Equal("S-1-5-21-0-0-0-1001", policy.TargetUserSid);
    }

    // ------------------------------------------------ activation and review

    [Fact]
    public void An_untrustworthy_policy_cannot_be_written_in_enforcing_mode()
    {
        var policy = Build(WithApps(("Kommandotolken", @"C:\Windows\System32\cmd.exe")));

        Assert.False(policy.CanActivate);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.Enabled));

        Assert.Contains("escape-surface-allowed", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Audit mode blocks nothing, so writing it is how somebody investigates a
    /// policy they were told not to turn on.
    /// </summary>
    [Fact]
    public void An_untrustworthy_policy_can_still_be_written_for_review()
    {
        var policy = Build(WithApps(("Kommandotolken", @"C:\Windows\System32\cmd.exe")));

        var xml = AppLockerPolicyWriter.Write(policy, AppLockerPolicyWriter.EnforcementMode.AuditOnly);

        Assert.Contains("AppLockerPolicy", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_policy_can_be_written_in_enforcing_mode()
    {
        var xml = AppLockerPolicyWriter.Write(Build(), AppLockerPolicyWriter.EnforcementMode.Enabled);

        Assert.Contains("AppLockerPolicy", xml, StringComparison.Ordinal);
        Assert.DoesNotContain(@"%WINDIR%\*", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_report_names_every_rule_and_its_reason()
    {
        var report = PolicyAuditReport.Write(Build());

        Assert.Contains("userinit.exe", report, StringComparison.Ordinal);
        Assert.Contains("KidShell", report, StringComparison.Ordinal);
        Assert.Contains("Varför:", report, StringComparison.Ordinal);
        Assert.Contains("kan aktiveras", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_report_leads_with_the_reason_a_policy_is_refused()
    {
        var report = PolicyAuditReport.Write(Build(WithApps(("Kommandotolken", @"C:\Windows\System32\cmd.exe"))));

        Assert.Contains("får INTE aktiveras", report, StringComparison.Ordinal);
        Assert.Contains("HINDER", report, StringComparison.Ordinal);
    }
}
