using System.Reflection;
using KidShell.Core.Security.Readiness;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The AppLocker capability model, after correcting a factual error.
///
/// An earlier version gated AppLocker on the edition and reported it as
/// unavailable on Home and Pro. Microsoft's current requirements table says
/// otherwise: as of KB 5024351, Windows 10 version 2004 and newer and all
/// Windows 11 versions enforce AppLocker policies on every edition. What still
/// varies by edition is the AppLocker CSP; what varies by machine is whether
/// the PowerShell module and the policy consoles are installed.
///
/// These tests pin that separation so the two cannot be recollapsed into one
/// boolean.
/// </summary>
public class AppControlCapabilityTests
{
    // ------------------------------------------------ enforcement is not edition-gated

    [Theory]
    [InlineData("Core")]                    // Home
    [InlineData("CoreSingleLanguage")]
    [InlineData("Professional")]
    [InlineData("ProfessionalEducation")]
    [InlineData("Enterprise")]
    [InlineData("Education")]
    [InlineData("IoTEnterprise")]
    public void Every_edition_on_Windows_11_can_enforce(string editionId)
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { EditionId = editionId });

        Assert.Equal(CapabilityState.Available, capabilities.AppControl.Enforcement);
        Assert.True(capabilities.SupportsAppLockerEnforcement);
    }

    [Fact]
    public void Enforcement_follows_the_build_number_not_the_edition()
    {
        // Same edition either side of the KB 5024351 boundary (build 19041).
        var before = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { BuildNumber = 19040 });

        var after = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { BuildNumber = 19041 });

        Assert.Equal(CapabilityState.Unavailable, before.AppControl.Enforcement);
        Assert.Equal(CapabilityState.Available, after.AppControl.Enforcement);
    }

    // ------------------------------------------------ the CSP is edition-gated

    [Theory]
    [InlineData("Professional", true)]
    [InlineData("ProfessionalEducation", true)]
    [InlineData("ProfessionalWorkstation", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Education", true)]
    [InlineData("IoTEnterprise", true)]
    [InlineData("Core", false)]
    [InlineData("CoreN", false)]
    public void The_CSP_follows_its_documented_edition_list(string editionId, bool expected)
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { EditionId = editionId });

        Assert.Equal(
            expected ? CapabilityState.Available : CapabilityState.Unavailable,
            capabilities.AppControl.Csp);
    }

    // ------------------------------------------------ machine-probed channels

    [Fact]
    public void PowerShell_management_comes_from_the_machine_not_the_edition()
    {
        var without = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());
        var with = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.WithAppLockerTooling(SecurityFixtures.Windows11Home()));

        Assert.Equal(CapabilityState.Unavailable, without.AppControl.PowerShellManagement);
        Assert.Equal(CapabilityState.Available, with.AppControl.PowerShellManagement);

        // Even Enterprise reports it as missing when the module is not there.
        var enterpriseWithoutModule = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Enterprise() with { AppLockerModuleAvailable = false });

        Assert.Equal(CapabilityState.Unavailable, enterpriseWithoutModule.AppControl.PowerShellManagement);
    }

    [Fact]
    public void Local_policy_readability_is_reported_separately()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with
            {
                AppLockerModuleAvailable = true,
                AppLockerLocalPolicyReadable = false
            });

        Assert.Equal(CapabilityState.Available, capabilities.AppControl.PowerShellManagement);
        Assert.Equal(CapabilityState.Unavailable, capabilities.AppControl.LocalPolicyReadable);
    }

    [Fact]
    public void The_management_console_is_its_own_channel()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { LocalSecurityPolicyUiPresent = true });

        Assert.Equal(CapabilityState.Available, capabilities.AppControl.ManagementUi);
        Assert.True(capabilities.AppControl.HasDeploymentChannel);
    }

    [Fact]
    public void The_enforcement_service_start_mode_is_carried_for_diagnostics()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.Equal("Manual", capabilities.AppControl.EnforcementServiceStartMode);

        // Reported, never acted on: KidShell does not start the service or
        // change its start type in this milestone or in this codebase.
        Assert.Equal(CapabilityState.Available, capabilities.AppControl.EnforcementService);
    }

    // ------------------------------------------------ the composite answers

    [Fact]
    public void Enforcement_and_deployment_are_not_the_same_question()
    {
        var home = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.True(home.SupportsAppLockerEnforcement);
        Assert.False(home.SupportsAppLockerDeployment);

        // The whole point of the correction: these two must be able to
        // disagree, which a single boolean made impossible.
        Assert.NotEqual(home.SupportsAppLockerEnforcement, home.SupportsAppLockerDeployment);
    }

    [Fact]
    public void A_Pro_machine_with_the_CSP_has_a_channel_without_local_tooling()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());

        Assert.Equal(CapabilityState.Unavailable, capabilities.AppControl.PowerShellManagement);
        Assert.Equal(CapabilityState.Unavailable, capabilities.AppControl.ManagementUi);
        Assert.Equal(CapabilityState.Available, capabilities.AppControl.Csp);
        Assert.True(capabilities.AppControl.HasDeploymentChannel);
    }

    // ------------------------------------------------ fail safe

    [Fact]
    public void Failed_detection_makes_every_app_control_answer_Unknown()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(WindowsSystemFacts.Unknown("registry unavailable"));
        var appControl = capabilities.AppControl;

        Assert.Equal(CapabilityState.Unknown, appControl.Enforcement);
        Assert.Equal(CapabilityState.Unknown, appControl.EnforcementService);
        Assert.Equal(CapabilityState.Unknown, appControl.PowerShellManagement);
        Assert.Equal(CapabilityState.Unknown, appControl.Csp);

        Assert.False(appControl.CanEnforce);
        Assert.False(appControl.HasDeploymentChannel);
    }

    [Fact]
    public void An_unknown_edition_still_reads_the_version_but_fails_safe_on_the_CSP()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { EditionId = "SomeFutureSku" });

        // The build is still readable, so enforcement is knowable.
        Assert.Equal(CapabilityState.Available, capabilities.AppControl.Enforcement);

        // The CSP depends on the edition, which is not, so it fails safe.
        Assert.Equal(CapabilityState.Unknown, capabilities.AppControl.Csp);
        Assert.False(capabilities.SupportsAppLockerDeployment);
    }

    // ------------------------------------------------ still no mutation path

    [Fact]
    public void The_app_control_model_exposes_no_way_to_change_anything()
    {
        // Every member is a read-only capability answer. If someone later adds
        // an Apply/Configure/Start/Enable member here, this fails - which is
        // the point, because AppIDSvc and the policy store are exactly what a
        // future milestone would be tempted to touch from the wrong place.
        // Property accessors and the record's own generated members are
        // filtered out; what is left is the surface someone actually wrote.
        var members = typeof(AppControlCapabilities)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodBase { IsSpecialName: true })
            .Where(m => !m.Name.StartsWith('<'))
            .ToArray();

        foreach (var forbidden in new[] { "Apply", "Configure", "Start", "Stop", "Enable", "Disable", "Write", "Install" })
        {
            Assert.DoesNotContain(
                members,
                m => m.Name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Capability_properties_are_read_only()
    {
        var settable = typeof(AppControlCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } set && !set.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            .ToArray();

        // init-only setters are fine; a plain public setter is not.
        Assert.Empty(settable);
    }
}
