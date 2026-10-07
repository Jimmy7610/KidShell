using KidShell.Core.Security.Storage;
using KidShell.Core.Security.Validation;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// THE SAFETY INTERLOCK.
///
/// Everything else in this file is about reporting honestly. This part is about
/// not destroying somebody's computer.
///
/// The validation tooling applies real Windows lockdown: accounts, access
/// lists, AppLocker, a restricted shell. Run on the wrong machine it takes away
/// the ability to sign in. A single switch guarding that is one copied command
/// line away from a family computer, so there is no switch: six conditions of
/// deliberately different KINDS must all hold at once, and each test below
/// removes exactly one of them.
/// </summary>
public class ValidationInterlockTests
{
    private static DeviceValidationConfig Config() => new()
    {
        MachineName = "KIDSHELL-TEST-01",
        DedicatedTestDevice = true,
        ParentAdminUser = "validator",
        ChildUser = "barn",
        ExpectedChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        ExpectedWindowsEdition = "Professional",
        ExpectedBuild = "26100",
        KidShellInstallRoot = @"C:\Program Files\KidShell",
        EvidenceDirectory = @"C:\KidShell-Validation"
    };

    private static InterlockFacts Facts() => new()
    {
        MachineName = "KIDSHELL-TEST-01",
        IsElevated = true,
        MarkerFilePresent = true,
        ConfirmationPhrase = ValidationInterlock.ConfirmationPhrase,
        IsDevelopmentBuild = false
    };

    [Fact]
    public void A_fully_prepared_dedicated_device_is_allowed()
    {
        var decision = ValidationInterlock.Evaluate(Config(), Facts());

        Assert.True(decision.Allowed, string.Join("; ", decision.Explain()));
        Assert.Empty(decision.Refusals);
    }

    [Fact]
    public void No_config_at_all_refuses()
    {
        var decision = ValidationInterlock.Evaluate(null, Facts());

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.ConfigMissing, decision.Refusals);
    }

    [Fact]
    public void A_config_that_does_not_declare_the_device_refuses()
    {
        var decision = ValidationInterlock.Evaluate(
            Config() with { DedicatedTestDevice = false }, Facts());

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.NotDeclaredDedicated, decision.Refusals);
    }

    [Fact]
    public void A_config_written_for_another_machine_refuses()
    {
        // The realistic accident: a prepared config copied from the test rig
        // onto the machine somebody actually works on.
        var decision = ValidationInterlock.Evaluate(
            Config() with { MachineName = "FAMILY-LAPTOP" }, Facts());

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.MachineNameMismatch, decision.Refusals);
    }

    [Fact]
    public void A_missing_marker_file_refuses()
    {
        var decision = ValidationInterlock.Evaluate(Config(), Facts() with { MarkerFilePresent = false });

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.MarkerFileMissing, decision.Refusals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("y")]
    [InlineData("I understand this is a dedicated kidshell test device")]
    [InlineData("I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST DEVICE.")]
    [InlineData("I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST  DEVICE")]
    public void Anything_but_the_exact_phrase_refuses(string typed)
    {
        // Case, punctuation and spacing all count. A phrase that can be
        // approximated is a phrase that can be guessed at in a hurry.
        var decision = ValidationInterlock.Evaluate(Config(), Facts() with { ConfirmationPhrase = typed });

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.ConfirmationPhraseWrong, decision.Refusals);
    }

    [Fact]
    public void Surrounding_whitespace_in_the_phrase_is_forgiven()
    {
        // A terminal paste usually carries some, and punishing that would send
        // people looking for a way around the prompt.
        var decision = ValidationInterlock.Evaluate(
            Config(), Facts() with { ConfirmationPhrase = $"  {ValidationInterlock.ConfirmationPhrase}\r\n" });

        Assert.True(decision.Allowed, string.Join("; ", decision.Explain()));
    }

    [Fact]
    public void An_unelevated_window_refuses()
    {
        var decision = ValidationInterlock.Evaluate(Config(), Facts() with { IsElevated = false });

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.NotElevated, decision.Refusals);
    }

    [Fact]
    public void A_development_build_refuses_even_on_a_genuine_test_device()
    {
        // The one condition that travels with the code rather than the
        // machine. A working copy must not be able to apply real lockdown
        // anywhere.
        var decision = ValidationInterlock.Evaluate(Config(), Facts() with { IsDevelopmentBuild = true });

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.DevelopmentBuild, decision.Refusals);
    }

    [Fact]
    public void Signs_of_a_working_machine_refuse()
    {
        var decision = ValidationInterlock.Evaluate(
            Config(),
            Facts() with { WorkingMachineSigns = ["joined to a domain", "12 user profiles"] });

        Assert.False(decision.Allowed);
        Assert.Contains(InterlockRefusal.LooksLikeAWorkingMachine, decision.Refusals);
    }

    [Fact]
    public void Every_single_condition_is_sufficient_to_refuse_on_its_own()
    {
        // The property that makes this an interlock. Written as one test over
        // all of them so a condition added later without a refusal path is
        // visible here rather than discovered on a device.
        var mutations = new (string What, DeviceValidationConfig? Config, InterlockFacts Facts)[]
        {
            ("no config", null, Facts()),
            ("not declared", Config() with { DedicatedTestDevice = false }, Facts()),
            ("wrong machine", Config() with { MachineName = "OTHER" }, Facts()),
            ("incomplete config", Config() with { ParentAdminUser = "" }, Facts()),
            ("same account twice", Config() with { ChildUser = "validator" }, Facts()),
            ("no marker", Config(), Facts() with { MarkerFilePresent = false }),
            ("no phrase", Config(), Facts() with { ConfirmationPhrase = "" }),
            ("not elevated", Config(), Facts() with { IsElevated = false }),
            ("development build", Config(), Facts() with { IsDevelopmentBuild = true }),
            ("working machine", Config(), Facts() with { WorkingMachineSigns = ["a user profile"] })
        };

        foreach (var (what, config, facts) in mutations)
        {
            Assert.False(
                ValidationInterlock.Evaluate(config, facts).Allowed,
                $"the interlock opened with: {what}");
        }
    }

    [Fact]
    public void Every_refusal_has_a_sentence_an_operator_can_act_on()
    {
        foreach (var refusal in Enum.GetValues<InterlockRefusal>())
        {
            var explanation = ValidationInterlock.Explain(refusal);

            Assert.False(string.IsNullOrWhiteSpace(explanation));
            Assert.NotEqual("Refused.", explanation);
        }
    }

    [Fact]
    public void The_marker_lives_outside_the_repository()
    {
        // A marker that travelled with a checkout would arrive on every
        // machine the checkout did.
        Assert.StartsWith(@"C:\ProgramData\", ValidationInterlock.MarkerPath, StringComparison.Ordinal);
        Assert.DoesNotContain("Projects", ValidationInterlock.MarkerPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_marker_says_what_it_authorises()
    {
        var marker = ValidationInterlock.MarkerTemplate("KIDSHELL-TEST-01", DateTimeOffset.UnixEpoch);

        Assert.Contains("may be destroyed", marker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KIDSHELL-TEST-01", marker, StringComparison.Ordinal);
        Assert.Contains("Delete this file", marker, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The validation config, as a declaration that has to hold together.</summary>
public class DeviceValidationConfigTests
{
    [Fact]
    public void The_shipped_template_refuses_as_written()
    {
        // It is a template. An unedited copy must not be mistaken for a
        // decision somebody made.
        var template = DeviceValidationConfig.Template();

        Assert.False(template.DedicatedTestDevice);
        Assert.False(template.IsComplete);
        Assert.NotEmpty(template.Problems());
    }

    [Fact]
    public void A_parent_and_child_that_are_the_same_account_is_a_problem()
    {
        var config = DeviceValidationConfig.Template() with
        {
            MachineName = "T", ParentAdminUser = "same", ChildUser = "SAME",
            EvidenceDirectory = @"C:\E"
        };

        // The entire architecture is the difference between these two. If they
        // are one account, every denial this validation checks would pass for
        // the wrong reason.
        Assert.Contains(config.Problems(),
            p => p.Contains("same account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_placeholder_left_in_place_is_a_problem()
    {
        var config = DeviceValidationConfig.Template() with
        {
            DedicatedTestDevice = true, EvidenceDirectory = @"C:\E"
        };

        Assert.Contains(config.Problems(),
            p => p.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-..\\..\\SOFTWARE")]
    [InlineData("administrator")]
    public void A_child_sid_that_is_not_a_sid_is_a_problem(string sid)
    {
        var config = Complete() with { ExpectedChildSid = sid };

        Assert.Contains(config.Problems(), p => p.Contains("SID", StringComparison.Ordinal));
    }

    [Fact]
    public void A_complete_config_has_no_problems()
    {
        Assert.Empty(Complete().Problems());
        Assert.True(Complete().IsComplete);
    }

    [Fact]
    public void It_round_trips_through_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Complete(), DeviceValidationConfig.Options);
        var parsed = DeviceValidationConfig.Parse(json);

        Assert.NotNull(parsed);
        Assert.Equal(Complete(), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Anything_unreadable_parses_to_nothing(string? json) =>
        Assert.Null(DeviceValidationConfig.Parse(json));

    [Fact]
    public void There_is_no_field_a_password_could_live_in()
    {
        var fields = typeof(DeviceValidationConfig).GetProperties().Select(p => p.Name.ToLowerInvariant());

        foreach (var forbidden in new[] { "password", "pin", "secret", "credential", "token" })
        {
            Assert.DoesNotContain(fields, f => f.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static DeviceValidationConfig Complete() => new()
    {
        MachineName = "KIDSHELL-TEST-01",
        DedicatedTestDevice = true,
        ParentAdminUser = "validator",
        ChildUser = "barn",
        ExpectedChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        ExpectedWindowsEdition = "Professional",
        ExpectedBuild = "26100",
        KidShellInstallRoot = @"C:\Program Files\KidShell",
        EvidenceDirectory = @"C:\KidShell-Validation"
    };
}

/// <summary>
/// The report, and specifically its refusal to flatter a run.
///
/// A validation report is read once, late, by somebody who wants it to say
/// yes. Every rule here exists to stop it saying yes about something nobody
/// checked.
/// </summary>
public class ValidationReportTests
{
    [Fact]
    public void A_run_that_did_nothing_is_incomplete_rather_than_passing()
    {
        var report = new ValidationReport();

        Assert.Equal(ValidationOutcome.Incomplete, report.Outcome);
        Assert.Equal("INCOMPLETE", ValidationReport.Label(report.Outcome));
    }

    [Fact]
    public void Every_required_stage_appears_even_when_it_did_not_run()
    {
        var report = new ValidationReport
        {
            Stages = [new ValidationStage { Name = "NAMED PIPE", Findings = [ValidationFinding.Pass("ok")] }]
        };

        var markdown = report.ToMarkdown();

        foreach (var stage in ValidationReport.RequiredStages)
        {
            Assert.Contains(stage, markdown, StringComparison.Ordinal);
        }

        // Ten of the eleven did not run, and the report says so eleven times
        // rather than once.
        Assert.Equal(10, report.AllStages().Count(s => s.Status == ValidationStageStatus.NotRun));
    }

    [Fact]
    public void A_skipped_stage_is_never_a_pass()
    {
        var report = new ValidationReport
        {
            Stages = [.. ValidationReport.RequiredStages.Select(name => new ValidationStage
            {
                Name = name,
                Findings = name == "SCREEN TIME"
                    ? [ValidationFinding.NotRun("the child account was not available")]
                    : [ValidationFinding.Pass("ok")]
            })]
        };

        Assert.Equal(ValidationOutcome.Incomplete, report.Outcome);
        Assert.Contains("SCREEN TIME: NOT RUN", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void One_failure_fails_the_whole_run()
    {
        var report = new ValidationReport
        {
            Stages = [.. ValidationReport.RequiredStages.Select(name => new ValidationStage
            {
                Name = name,
                Findings = name == "PROTECTED STORE ACL"
                    ? [ValidationFinding.Fail("the child could delete parent-policy.json")]
                    : [ValidationFinding.Pass("ok")]
            })]
        };

        Assert.Equal(ValidationOutcome.Fail, report.Outcome);

        var markdown = report.ToMarkdown();

        Assert.Contains("OVERALL: FAIL", markdown, StringComparison.Ordinal);
        Assert.Contains("could delete parent-policy.json", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_platform_does_not_fail_a_run()
    {
        // A product working within an edition's limits has not failed. It has
        // to be visible, which is what NOT SUPPORTED is for, and it must not
        // be mistaken for a failure either.
        var report = new ValidationReport
        {
            Stages = [.. ValidationReport.RequiredStages.Select(name => new ValidationStage
            {
                Name = name,
                Findings = name.Contains("CAPABILITY", StringComparison.Ordinal)
                    ? [ValidationFinding.NotSupported("Windows Home")]
                    : [ValidationFinding.Pass("ok")]
            })]
        };

        Assert.Equal(ValidationOutcome.Pass, report.Outcome);
        Assert.Contains("APPLOCKER CAPABILITY: NOT SUPPORTED", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_is_reported_without_failing_anything()
    {
        var stage = new ValidationStage
        {
            Name = "SECURITYHOST SERVICE",
            Findings = [ValidationFinding.Pass("running"), ValidationFinding.Warn("no restart action")]
        };

        Assert.Equal(ValidationStageStatus.Pass, stage.Status);

        var markdown = new ValidationReport { Stages = [stage] }.ToMarkdown();

        Assert.Contains("no restart action", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrun_findings_appear_under_warnings_so_they_are_not_silent()
    {
        var markdown = new ValidationReport
        {
            Stages =
            [
                new ValidationStage
                {
                    Name = "CHILD AUTHORIZATION",
                    Findings = [ValidationFinding.NotRun("no second account on this machine")]
                }
            ]
        }.ToMarkdown();

        Assert.Contains("(not run)", markdown, StringComparison.Ordinal);
        Assert.Contains("no second account", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_headings_are_the_ones_the_format_requires()
    {
        var markdown = new ValidationReport().ToMarkdown();

        foreach (var heading in new[]
                 {
                     "# KidShell Dedicated Device Validation Report",
                     "## Result", "## Critical failures", "## Warnings",
                     "## Evidence", "## Remaining blockers"
                 })
        {
            Assert.Contains(heading, markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Missing_metadata_is_marked_as_missing_rather_than_left_blank()
    {
        Assert.Contains("Machine: (not recorded)", new ValidationReport().ToMarkdown(), StringComparison.Ordinal);
    }
}

/// <summary>Comparing a real machine against the plans the product publishes.</summary>
public class ValidationExpectationTests
{
    private static readonly ProtectedStorePlan Plan = ProtectedStorePlan.For(@"C:\ProgramData");

    private static ProtectedStoreObservation Correct() => new()
    {
        Directory = @"C:\ProgramData\KidShell\policy",
        Exists = true,
        Owner = "BUILTIN\\Administrators",
        InheritanceRemoved = true,
        Effective = new Dictionary<ProtectedStorePrincipal, ProtectedStoreRights>
        {
            [ProtectedStorePrincipal.Administrators] = ProtectedStoreRights.FullControl,
            [ProtectedStorePrincipal.System] = ProtectedStoreRights.FullControl,
            [ProtectedStorePrincipal.Child] = ProtectedStoreRights.ReadOnly
        }
    };

    private static ProtectedStoreProbeOutcome[] AllDenied() =>
        [.. ProtectedStoreExpectation.RequiredDenials.Select(o => new ProtectedStoreProbeOutcome(o, true))];

    [Fact]
    public void A_correctly_permissioned_store_with_every_probe_denied_passes()
    {
        var findings = ProtectedStoreExpectation.Compare(Plan, Correct(), AllDenied());
        var stage = new ValidationStage { Name = "x", Findings = findings };

        Assert.Equal(ValidationStageStatus.Pass, stage.Status);
        Assert.Single(findings);
        Assert.Equal(ValidationStageStatus.Pass, findings[0].Status);
        Assert.Contains("all required child", findings[0].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_store_that_does_not_exist_is_not_run_rather_than_failed()
    {
        var findings = ProtectedStoreExpectation.Compare(
            Plan, Correct() with { Exists = false }, AllDenied());

        Assert.All(findings, f => Assert.Equal(ValidationStageStatus.NotRun, f.Status));
    }

    [Fact]
    public void A_child_with_write_access_fails()
    {
        var findings = ProtectedStoreExpectation.Compare(
            Plan,
            Correct() with
            {
                Effective = new Dictionary<ProtectedStorePrincipal, ProtectedStoreRights>
                {
                    [ProtectedStorePrincipal.Administrators] = ProtectedStoreRights.FullControl,
                    [ProtectedStorePrincipal.System] = ProtectedStoreRights.FullControl,
                    [ProtectedStorePrincipal.Child] = ProtectedStoreRights.Read | ProtectedStoreRights.Write
                }
            },
            AllDenied());

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("Child", StringComparison.Ordinal));
    }

    [Fact]
    public void A_principal_the_plan_does_not_mention_fails()
    {
        var effective = Correct().Effective.ToDictionary(e => e.Key, e => e.Value);
        effective[ProtectedStorePrincipal.Users] = ProtectedStoreRights.Read;

        var findings = ProtectedStoreExpectation.Compare(
            Plan, Correct() with { Effective = effective }, AllDenied());

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("Users", StringComparison.Ordinal));
    }

    [Fact]
    public void Inheritance_left_in_place_fails()
    {
        var findings = ProtectedStoreExpectation.Compare(
            Plan, Correct() with { InheritanceRemoved = false }, AllDenied());

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("Inherited", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unprobed_denial_is_not_run_rather_than_assumed()
    {
        // The difference between an access list that reads correctly and a
        // store that actually refuses. Only a real attempt is evidence.
        var findings = ProtectedStoreExpectation.Compare(Plan, Correct(), []);

        Assert.Equal(
            ProtectedStoreExpectation.RequiredDenials.Length,
            findings.Count(f => f.Status == ValidationStageStatus.NotRun));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("overwrite")]
    [InlineData("append")]
    [InlineData("rename")]
    [InlineData("delete")]
    public void Any_single_probe_that_succeeded_fails_the_stage(string operation)
    {
        var probes = ProtectedStoreExpectation.RequiredDenials
            .Select(o => new ProtectedStoreProbeOutcome(
                o, !string.Equals(o, operation, StringComparison.Ordinal)))
            .ToArray();

        var findings = ProtectedStoreExpectation.Compare(Plan, Correct(), probes);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains(operation, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Append_is_one_of_the_operations_that_must_be_denied()
    {
        // The one people forget. Write-denied and append-allowed is enough to
        // corrupt a policy document into unreadable, and this product treats
        // unreadable as a reason to fail closed - so a child could turn "I may
        // not change the policy" into "there is no usable policy".
        Assert.Contains("append", ProtectedStoreExpectation.RequiredDenials);
    }

    // ------------------------------------------------------- the service

    private static SecurityHostObservation CorrectService() => new()
    {
        Installed = true,
        Running = true,
        StartType = "Automatic",
        Account = "LocalSystem",
        ImagePath = @"""C:\Program Files\KidShell\KidShell.SecurityHost.exe""",
        ImageExists = true,
        ImageSha256 = new string('a', 64),
        ImageChildWritable = false,
        RecoveryConfigured = true,
        ChildStopDenied = true,
        ChildReconfigureDenied = true
    };

    private const string InstallRoot = @"C:\Program Files\KidShell";

    [Fact]
    public void A_correctly_installed_service_passes()
    {
        var findings = SecurityHostExpectation.Compare(CorrectService(), InstallRoot);

        Assert.DoesNotContain(findings, f => f.Status == ValidationStageStatus.Fail);
        Assert.DoesNotContain(findings, f => f.Status == ValidationStageStatus.NotRun);
    }

    [Fact]
    public void An_absent_service_is_not_run_rather_than_failed()
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { Installed = false }, InstallRoot);

        Assert.All(findings, f => Assert.Equal(ValidationStageStatus.NotRun, f.Status));
    }

    [Fact]
    public void An_installed_service_that_is_not_running_fails()
    {
        // Worse than missing: the product would look configured and every
        // protected write would fail.
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { Running = false }, InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("not running", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Disabled")]
    public void A_service_that_does_not_start_automatically_fails(string startType)
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { StartType = startType }, InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("Automatic", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NT AUTHORITY\\NetworkService")]
    [InlineData(".\\validator")]
    [InlineData("")]
    public void A_service_running_as_anything_but_localsystem_fails(string account)
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { Account = account }, InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("LocalSystem", StringComparison.Ordinal));
    }

    [Fact]
    public void The_system_account_spelled_either_way_is_accepted()
    {
        foreach (var account in new[] { "LocalSystem", @"NT AUTHORITY\SYSTEM" })
        {
            var findings = SecurityHostExpectation.Compare(
                CorrectService() with { Account = account }, InstallRoot);

            Assert.DoesNotContain(findings, f => f.Detail.Contains("must run as", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(@"C:\Users\barn\KidShell.SecurityHost.exe")]
    [InlineData(@"C:\Temp\KidShell.SecurityHost.exe")]
    [InlineData(@"""C:\Program Files\KidShell\..\..\Users\barn\KidShell.SecurityHost.exe""")]
    public void An_image_the_child_could_reach_fails(string imagePath)
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { ImagePath = imagePath }, InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("install root", StringComparison.Ordinal));
    }

    [Fact]
    public void A_child_writable_binary_fails()
    {
        // A LocalSystem service whose image the child can replace is a
        // privilege escalation with a service name, and no access list on the
        // pipe would matter.
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { ImageChildWritable = true }, InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("write the service binary", StringComparison.Ordinal));
    }

    [Fact]
    public void Arguments_on_the_registration_fail()
    {
        // The service takes none, so anything there changes what Windows runs
        // as SYSTEM at every boot.
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with
            {
                ImagePath = @"""C:\Program Files\KidShell\KidShell.SecurityHost.exe"" --stdin"
            },
            InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unprobed_child_denial_is_not_run_rather_than_assumed()
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { ChildStopDenied = null, ChildReconfigureDenied = null }, InstallRoot);

        Assert.Equal(2, findings.Count(f => f.Status == ValidationStageStatus.NotRun));
    }

    [Fact]
    public void A_child_that_stopped_the_service_fails()
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { ChildStopDenied = false }, InstallRoot);

        Assert.Contains(findings, f => f.Status == ValidationStageStatus.Fail
                                       && f.Detail.Contains("STOPPED", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_restart_action_is_a_warning_and_not_a_failure()
    {
        var findings = SecurityHostExpectation.Compare(
            CorrectService() with { RecoveryConfigured = false }, InstallRoot);

        Assert.Contains(findings, f => f.IsWarning);
        Assert.DoesNotContain(findings, f => f.Status == ValidationStageStatus.Fail);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\KidShell\x.exe""", @"C:\Program Files\KidShell\x.exe")]
    [InlineData(@"C:\KidShell\x.exe", @"C:\KidShell\x.exe")]
    [InlineData(@"C:\KidShell\x.exe --flag", @"C:\KidShell\x.exe")]
    public void The_executable_is_read_out_of_a_registration_correctly(string registration, string expected) =>
        Assert.Equal(expected, SecurityHostExpectation.ExecutableFrom(registration));
}

/// <summary>Edition limits, reported rather than wished away.</summary>
public class PlatformCapabilityTests
{
    private static PlatformFacts Home() => new()
    {
        EditionId = "Core",
        ProductName = "Windows 11 Home",
        Build = 26100,
        AppIdServicePresent = true,
        AppIdServiceStartType = "Manual"
    };

    private static PlatformFacts Pro() => new()
    {
        EditionId = "Professional",
        ProductName = "Windows 11 Pro",
        Build = 26100,
        AppIdServicePresent = true,
        AppIdServiceStartType = "Automatic",
        AppLockerCmdletsPresent = true,
        AppLockerPolicyReadable = true,
        AssignedAccessCmdletsPresent = true,
        MultiAppKioskAvailable = true
    };

    [Theory]
    [InlineData("Core")]
    [InlineData("CoreSingleLanguage")]
    [InlineData("CoreN")]
    public void Windows_home_cannot_enforce_applocker(string edition)
    {
        // The single most damaging overclaim this product could make. A parent
        // who believes the computer blocks unapproved programs supervises
        // less.
        var verdict = AppLockerCapability.Evaluate(Home() with { EditionId = edition });

        Assert.Equal(CapabilitySupport.NotSupported, verdict.Support);
        Assert.Equal("NOT_SUPPORTED", verdict.Label);
        Assert.Contains("cannot enforce", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_home_still_cannot_enforce_applocker_with_every_cmdlet_present()
    {
        // Cmdlets present and a policy readable do not make enforcement
        // something a parent can rely on, because there is no supported
        // deployment channel.
        var verdict = AppLockerCapability.Evaluate(Home() with
        {
            AppIdServiceStartType = "Automatic",
            AppLockerCmdletsPresent = true,
            AppLockerPolicyReadable = true
        });

        Assert.Equal(CapabilitySupport.NotSupported, verdict.Support);
    }

    [Fact]
    public void Windows_pro_with_the_identity_service_running_is_supported()
    {
        var verdict = AppLockerCapability.Evaluate(Pro());

        Assert.Equal(CapabilitySupport.Supported, verdict.Support);
        Assert.Equal("SUPPORTED", verdict.Label);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Disabled")]
    [InlineData("")]
    public void Applocker_without_the_identity_service_is_only_partial(string startType)
    {
        // Rules do nothing until that service runs, and a report that called
        // this SUPPORTED would be describing rules nobody is enforcing.
        var verdict = AppLockerCapability.Evaluate(Pro() with { AppIdServiceStartType = startType });

        Assert.Equal(CapabilitySupport.PartiallySupported, verdict.Support);
        Assert.Contains("not active", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applocker_without_the_cmdlets_is_only_partial()
    {
        var verdict = AppLockerCapability.Evaluate(Pro() with { AppLockerCmdletsPresent = false });

        Assert.Equal(CapabilitySupport.PartiallySupported, verdict.Support);
    }

    [Fact]
    public void Windows_home_cannot_do_assigned_access()
    {
        var verdict = AssignedAccessCapability.Evaluate(Home());

        Assert.Equal(CapabilitySupport.NotSupported, verdict.Support);
        Assert.Contains("Standard Mode", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_older_than_the_multi_app_kiosk_is_not_supported()
    {
        var verdict = AssignedAccessCapability.Evaluate(
            Pro() with { Build = AssignedAccessCapability.MinimumBuild - 1 });

        Assert.Equal(CapabilitySupport.NotSupported, verdict.Support);
    }

    [Fact]
    public void Single_app_kiosk_alone_is_only_partial()
    {
        // Single-app is not what this product wants: a child needs more than
        // one program.
        var verdict = AssignedAccessCapability.Evaluate(Pro() with { MultiAppKioskAvailable = false });

        Assert.Equal(CapabilitySupport.PartiallySupported, verdict.Support);
        Assert.Contains("more than one program", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_pro_supports_assigned_access()
    {
        Assert.Equal(CapabilitySupport.Supported, AssignedAccessCapability.Evaluate(Pro()).Support);
    }

    [Fact]
    public void Every_verdict_explains_itself_in_a_sentence()
    {
        foreach (var facts in new[] { Home(), Pro(), new PlatformFacts() })
        {
            foreach (var verdict in new[]
                     {
                         AppLockerCapability.Evaluate(facts),
                         AssignedAccessCapability.Evaluate(facts)
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
                Assert.True(verdict.Reason.Length > 30, $"too terse: {verdict.Reason}");
            }
        }
    }

    [Fact]
    public void An_unknown_edition_is_not_supported()
    {
        // The safe answer for something this build has never heard of.
        var verdict = AppLockerCapability.Evaluate(new PlatformFacts { EditionId = "SomethingNew" });

        Assert.Equal(CapabilitySupport.NotSupported, verdict.Support);
    }
}

/// <summary>Evidence that can be handed to somebody without handing over secrets.</summary>
public class EvidenceRedactionTests
{
    [Fact]
    public void A_pin_hash_and_salt_do_not_survive()
    {
        var result = EvidenceRedaction.Redact(
            """{"parentPin":{"hash":"SECRET-HASH","salt":"SECRET-SALT","iterations":210000}}""");

        Assert.True(result.Parsed);
        Assert.DoesNotContain("SECRET-HASH", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SALT", result.Json, StringComparison.Ordinal);

        // One field, not three: "parentPin" is itself a secret name, so the
        // whole object goes rather than being walked into. That is the safer
        // direction - a container whose name says "secret" does not get the
        // chance to have an unlisted field inside it.
        Assert.Equal(1, result.RedactedFields);
        Assert.DoesNotContain("210000", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_value_survives()
    {
        var result = EvidenceRedaction.Redact("""{"usedSeconds":1234,"localDate":"2026-10-01"}""");

        Assert.Contains("1234", result.Json, StringComparison.Ordinal);
        Assert.Contains("2026-10-01", result.Json, StringComparison.Ordinal);
        Assert.Equal(0, result.RedactedFields);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("parentPinAttempt")]
    [InlineData("PIN_HASH")]
    [InlineData("salt")]
    [InlineData("Password")]
    [InlineData("apiToken")]
    [InlineData("parentCapability")]
    [InlineData("protectedPayload")]
    [InlineData("privateKey")]
    public void Every_secret_shaped_field_name_is_caught(string field) =>
        Assert.True(EvidenceRedaction.IsSecret(field), field);

    [Theory]
    [InlineData("usedSeconds")]
    [InlineData("sequence")]
    [InlineData("localDate")]
    [InlineData("sha256")]
    [InlineData("imageSha256")]
    [InlineData("digest")]
    [InlineData("stagedDigest")]
    public void A_safe_field_name_is_not_caught(string field) =>
        Assert.False(EvidenceRedaction.IsSecret(field), field);

    [Fact]
    public void An_existing_digest_is_not_digested_again()
    {
        // Two runs have to remain comparable. Re-hashing a hash would make
        // "did this change across the reboot" unanswerable.
        var result = EvidenceRedaction.Redact("""{"imageSha256":"abc123"}""");

        Assert.Contains("abc123", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_and_arrays_are_walked()
    {
        var result = EvidenceRedaction.Redact(
            """{"runs":[{"state":{"pinSalt":"DEEP-SECRET"}},{"state":{"pinSalt":"ALSO-SECRET"}}]}""");

        Assert.DoesNotContain("DEEP-SECRET", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("ALSO-SECRET", result.Json, StringComparison.Ordinal);
        Assert.Equal(2, result.RedactedFields);
    }

    [Fact]
    public void A_null_secret_stays_null_rather_than_becoming_a_digest()
    {
        // "No PIN was configured" and "there was one and we are not telling
        // you" are different facts, and flattening them would make the
        // evidence lie in the more flattering direction.
        var result = EvidenceRedaction.Redact("""{"pinHash":null}""");

        Assert.Contains("null", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256:", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_secret_redacts_to_the_same_digest()
    {
        var first = EvidenceRedaction.Redact("""{"pinHash":"same"}""").Json;
        var second = EvidenceRedaction.Redact("""{"pinHash":"same"}""").Json;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_secrets_redact_to_different_digests()
    {
        Assert.NotEqual(
            EvidenceRedaction.Redact("""{"pinHash":"a"}""").Json,
            EvidenceRedaction.Redact("""{"pinHash":"b"}""").Json);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unparseable_document_is_refused_rather_than_published(string? json) =>
        Assert.False(EvidenceRedaction.Redact(json).Parsed);

    [Fact]
    public void An_object_valued_secret_is_replaced_whole()
    {
        var result = EvidenceRedaction.Redact(
            """{"protectedPayload":{"parentPin":{"hash":"INNER"},"apps":["a"]}}""");

        Assert.DoesNotContain("INNER", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("apps", result.Json, StringComparison.Ordinal);
    }
}

/// <summary>Resuming a run across a restart, and refusing to resume the wrong one.</summary>
public class RebootCheckpointTests
{
    private static RebootCheckpoint Checkpoint() => new()
    {
        RunId = "20261001-120000",
        MachineName = "KIDSHELL-TEST-01",
        Stage = "SCREEN TIME",
        WrittenUtc = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        ServiceRunning = true,
        ChildSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        StateDigests = new Dictionary<string, string>
        {
            ["screen-time-state.json"] = "aaa",
            ["pin-throttle.json"] = "bbb"
        },
        MachineStateHash = "cccc"
    };

    [Fact]
    public void It_round_trips()
    {
        var load = RebootCheckpoint.Load(Checkpoint().ToJson(), "KIDSHELL-TEST-01", "20261001-120000");

        Assert.True(load.CanResume);
        Assert.Equal("SCREEN TIME", load.Checkpoint!.Stage);
        Assert.Equal(2, load.Checkpoint.StateDigests.Count);
    }

    [Fact]
    public void A_checkpoint_from_another_machine_is_not_resumed()
    {
        // Resuming it would compare one machine's state against another's
        // expectations and report a pass.
        var load = RebootCheckpoint.Load(Checkpoint().ToJson(), "FAMILY-LAPTOP", "20261001-120000");

        Assert.False(load.CanResume);
        Assert.Contains("KIDSHELL-TEST-01", load.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_checkpoint_from_another_run_is_not_resumed()
    {
        var load = RebootCheckpoint.Load(Checkpoint().ToJson(), "KIDSHELL-TEST-01", "20261002-090000");

        Assert.False(load.CanResume);
    }

    [Fact]
    public void A_checkpoint_from_a_future_schema_is_not_resumed()
    {
        var json = (Checkpoint() with
        {
            SchemaVersion = RebootCheckpoint.CurrentSchemaVersion + 1
        }).ToJson();

        var load = RebootCheckpoint.Load(json, "KIDSHELL-TEST-01", "20261001-120000");

        Assert.False(load.CanResume);
        Assert.Contains("schema", load.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_checkpoint_with_no_stage_is_not_resumed()
    {
        var load = RebootCheckpoint.Load(
            (Checkpoint() with { Stage = "" }).ToJson(), "KIDSHELL-TEST-01", "20261001-120000");

        Assert.False(load.CanResume);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{}")]
    public void Anything_unreadable_is_not_resumed(string? json) =>
        Assert.False(RebootCheckpoint.Load(json, "KIDSHELL-TEST-01", "20261001-120000").CanResume);

    [Fact]
    public void Nothing_changed_across_a_reboot_is_an_empty_list()
    {
        var now = new Dictionary<string, string>
        {
            ["screen-time-state.json"] = "aaa",
            ["pin-throttle.json"] = "bbb"
        };

        Assert.Empty(Checkpoint().Changed(now));
    }

    [Fact]
    public void A_changed_document_is_named()
    {
        var now = new Dictionary<string, string>
        {
            ["screen-time-state.json"] = "DIFFERENT",
            ["pin-throttle.json"] = "bbb"
        };

        Assert.Equal(["screen-time-state.json"], Checkpoint().Changed(now));
    }

    [Fact]
    public void A_document_that_vanished_across_a_reboot_counts_as_changed()
    {
        // Exactly the kind of thing this is looking for: a PIN cooldown that
        // is simply gone after a restart.
        var now = new Dictionary<string, string> { ["screen-time-state.json"] = "aaa" };

        Assert.Equal(["pin-throttle.json"], Checkpoint().Changed(now));
    }

    [Fact]
    public void A_document_that_appeared_counts_as_changed()
    {
        var now = Checkpoint().StateDigests.ToDictionary(e => e.Key, e => e.Value);
        now["provisioned.json"] = "new";

        Assert.Equal(["provisioned.json"], Checkpoint().Changed(now));
    }

    [Fact]
    public void It_records_no_secret()
    {
        var fields = typeof(RebootCheckpoint).GetProperties().Select(p => p.Name.ToLowerInvariant());

        foreach (var forbidden in new[] { "password", "pin", "hash256", "secret", "payload" })
        {
            Assert.DoesNotContain(fields, f => f.Equals(forbidden, StringComparison.Ordinal));
        }

        // State is carried as digests, which is the whole point of the type.
        Assert.Contains("statedigests", fields);
    }
}
