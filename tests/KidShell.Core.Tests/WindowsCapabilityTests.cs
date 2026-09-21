using KidShell.Core.Security.Readiness;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Edition and capability mapping.
///
/// These run entirely from fixture data, so editions this machine is not
/// (Pro, Enterprise, Education) are covered just as well as the one it is.
/// </summary>
public class WindowsCapabilityTests
{
    // ------------------------------------------------ edition mapping

    [Theory]
    [InlineData("Core", WindowsEdition.Home)]
    [InlineData("CoreN", WindowsEdition.Home)]
    [InlineData("CoreSingleLanguage", WindowsEdition.Home)]
    [InlineData("CoreCountrySpecific", WindowsEdition.Home)]
    [InlineData("Professional", WindowsEdition.Pro)]
    [InlineData("ProfessionalN", WindowsEdition.Pro)]
    [InlineData("ProfessionalEducation", WindowsEdition.ProEducation)]
    [InlineData("ProfessionalWorkstation", WindowsEdition.ProForWorkstations)]
    [InlineData("Enterprise", WindowsEdition.Enterprise)]
    [InlineData("EnterpriseS", WindowsEdition.Enterprise)]
    [InlineData("Education", WindowsEdition.Education)]
    [InlineData("IoTEnterprise", WindowsEdition.IoTEnterprise)]
    [InlineData("ServerStandard", WindowsEdition.Server)]
    public void Edition_ids_map_to_normalized_editions(string editionId, WindowsEdition expected) =>
        Assert.Equal(expected, WindowsEditionMap.FromEditionId(editionId));

    [Theory]
    [InlineData("core")]
    [InlineData("  Core  ")]
    [InlineData("CORE")]
    public void Edition_id_matching_ignores_case_and_padding(string editionId) =>
        Assert.Equal(WindowsEdition.Home, WindowsEditionMap.FromEditionId(editionId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomeFutureSku")]
    [InlineData("Ultimate")]
    public void An_unrecognised_edition_is_Unknown_and_never_guessed_as_Pro(string? editionId)
    {
        var edition = WindowsEditionMap.FromEditionId(editionId);

        Assert.Equal(WindowsEdition.Unknown, edition);
        Assert.NotEqual(WindowsEdition.Pro, edition);
    }

    // ------------------------------------------------ generation

    [Theory]
    [InlineData(26200, WindowsGeneration.Windows11)]
    [InlineData(22000, WindowsGeneration.Windows11)]
    [InlineData(21999, WindowsGeneration.Windows10)]
    [InlineData(19045, WindowsGeneration.Windows10)]
    [InlineData(10240, WindowsGeneration.Windows10)]
    [InlineData(7601, WindowsGeneration.Legacy)]
    [InlineData(0, WindowsGeneration.Unknown)]
    public void Generation_comes_from_the_build_number(int build, WindowsGeneration expected) =>
        Assert.Equal(expected, WindowsEditionMap.FromBuild(build));

    [Fact]
    public void The_display_name_ignores_the_misleading_ProductName_value()
    {
        // Windows 11 reports ProductName "Windows 10 Home". Anything derived
        // from that string would be wrong on every Windows 11 machine, so the
        // display name is composed from the build and EditionID instead.
        var facts = SecurityFixtures.Windows11Home();
        var capabilities = WindowsCapabilityAnalyzer.Analyze(facts);

        Assert.Equal("Windows 10 Home", facts.ProductName);
        Assert.Equal("Windows 11 Home", capabilities.EditionDisplayName);
    }

    [Fact]
    public void An_unknown_edition_is_named_honestly()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { EditionId = "MysterySku" });

        Assert.Equal(WindowsEdition.Unknown, capabilities.Edition);
        Assert.Contains("okänd", capabilities.EditionDisplayName, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ Home

    [Fact]
    public void Home_does_not_support_assigned_access()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.Equal(WindowsEdition.Home, capabilities.Edition);
        Assert.Equal(CapabilityState.Unavailable, capabilities.AssignedAccess);
        Assert.False(capabilities.SupportsAssignedAccess);
        Assert.False(capabilities.SupportsSecureMode);
    }

    [Fact]
    public void Home_is_recommended_Standard_mode()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.Equal(SecurityMode.Standard, capabilities.RecommendedSecurityMode);
        Assert.Contains(capabilities.Warnings, w => w.Contains("Assigned Access", StringComparison.Ordinal));
    }

    [Fact]
    public void Home_still_has_KidShells_own_app_allowlist()
    {
        // Standard mode has a real app-control story; it is just KidShell's,
        // not Windows'. Reporting it as AppLocker would be the overclaim.
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.Equal(CapabilityState.Available, capabilities.KidShellAppAllowlist);
        Assert.Equal(CapabilityState.Unavailable, capabilities.AppLocker);
    }

    // ------------------------------------------------ Pro

    [Fact]
    public void Pro_supports_assigned_access_and_secure_mode()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());

        Assert.Equal(CapabilityState.Available, capabilities.AssignedAccess);
        Assert.True(capabilities.SupportsSecureMode);
        Assert.Equal(SecurityMode.Secure, capabilities.RecommendedSecurityMode);
    }

    [Fact]
    public void Pro_does_not_claim_AppLocker()
    {
        // Assigned Access and AppLocker are separate features with separate
        // edition requirements; Pro has the first and not the second.
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Pro());

        Assert.True(capabilities.SupportsAssignedAccess);
        Assert.False(capabilities.SupportsAppLocker);
        Assert.Equal(CapabilityState.Unavailable, capabilities.AppLocker);
    }

    // ------------------------------------------------ Enterprise / Education

    [Fact]
    public void Enterprise_supports_both_assigned_access_and_applocker()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Enterprise());

        Assert.Equal(CapabilityState.Available, capabilities.AssignedAccess);
        Assert.Equal(CapabilityState.Available, capabilities.AppLocker);
        Assert.Equal(SecurityMode.Secure, capabilities.RecommendedSecurityMode);
    }

    [Fact]
    public void Education_supports_both_assigned_access_and_applocker()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Education());

        Assert.True(capabilities.SupportsAssignedAccess);
        Assert.True(capabilities.SupportsAppLocker);
    }

    // ------------------------------------------------ fail-safe

    [Fact]
    public void An_unknown_edition_reports_capabilities_as_Unknown_not_Available()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { EditionId = "MysterySku" });

        Assert.Equal(CapabilityState.Unknown, capabilities.AssignedAccess);
        Assert.Equal(CapabilityState.Unknown, capabilities.AppLocker);
        Assert.False(capabilities.SupportsSecureMode);
        Assert.False(capabilities.SupportsAssignedAccess);
    }

    [Fact]
    public void Failed_detection_falls_back_to_the_least_capable_answer()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(WindowsSystemFacts.Unknown("registry unavailable"));

        Assert.True(capabilities.DetectionFailed);
        Assert.Equal(CapabilityState.Unknown, capabilities.AssignedAccess);
        Assert.False(capabilities.SupportsSecureMode);
        Assert.NotEqual(SecurityMode.Secure, capabilities.RecommendedSecurityMode);
        Assert.NotEmpty(capabilities.Warnings);
    }

    [Fact]
    public void Windows_older_than_10_is_blocked()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Pro() with { BuildNumber = 7601 });

        Assert.Equal(WindowsGeneration.Legacy, capabilities.Generation);
        Assert.NotEmpty(capabilities.Blockers);
        Assert.Equal(SecurityMode.Development, capabilities.RecommendedSecurityMode);
    }

    // ------------------------------------------------ UAC

    [Fact]
    public void Uac_enabled_is_reported_as_enabled()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.True(capabilities.IsUacEnabled);
        Assert.DoesNotContain(capabilities.Blockers, b => b.Contains("UAC", StringComparison.Ordinal));
    }

    [Fact]
    public void Uac_disabled_blocks_secure_mode_even_on_Pro()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Pro() with { IsUacEnabled = false });

        // The edition supports Assigned Access, but without UAC a separate
        // child account is not meaningfully separated, so Secure would be a
        // claim KidShell could not back up.
        Assert.True(capabilities.SupportsAssignedAccess);
        Assert.False(capabilities.SupportsSecureMode);
        Assert.Contains(capabilities.Blockers, b => b.Contains("UAC", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_uac_is_a_warning_rather_than_a_silent_pass()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Pro() with { IsUacEnabled = null });

        Assert.Null(capabilities.IsUacEnabled);
        Assert.False(capabilities.SupportsSecureMode);
        Assert.Contains(capabilities.Warnings, w => w.Contains("UAC", StringComparison.Ordinal));
    }

    // ------------------------------------------------ current user

    [Fact]
    public void An_administrator_account_is_reported_as_administrator()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home(),
            [SecurityFixtures.Admin("Jimmy")]);

        Assert.True(capabilities.CurrentUserIsAdministrator);
        Assert.Equal("Jimmy", capabilities.CurrentUserName);
    }

    [Fact]
    public void An_unelevated_administrator_is_still_an_administrator()
    {
        // The real shape of this machine: the account is in Administrators,
        // but UAC filters the SID out of the process token entirely. Reading
        // only the token would report a real administrator as a standard user
        // and invent a blocker that does not exist.
        var facts = SecurityFixtures.Windows11Home();

        Assert.False(facts.TokenShowsAdministrator);
        Assert.False(facts.IsProcessElevated);

        var capabilities = WindowsCapabilityAnalyzer.Analyze(facts, [SecurityFixtures.Admin("Jimmy")]);

        Assert.True(capabilities.CurrentUserIsAdministrator);
        Assert.False(capabilities.IsProcessElevated);
        Assert.DoesNotContain(capabilities.Warnings, w => w.Contains("standardanvändare", StringComparison.Ordinal));
    }

    [Fact]
    public void An_elevated_process_is_an_administrator_without_the_account_list()
    {
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { IsProcessElevated = true });

        Assert.True(capabilities.CurrentUserIsAdministrator);
        Assert.True(capabilities.IsProcessElevated);
    }

    [Fact]
    public void A_genuine_standard_user_is_warned_about_setup_rights()
    {
        // The signed-in account exists in the list but is not an administrator.
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { CurrentUserName = "Barn", CurrentUserSid = "S-1-5-21-1-2-3-1500" },
            [SecurityFixtures.Admin("Jimmy"), SecurityFixtures.Standard("Barn") with { Sid = "S-1-5-21-1-2-3-1500" }]);

        Assert.False(capabilities.CurrentUserIsAdministrator);
        Assert.Contains(capabilities.Warnings, w => w.Contains("standardanvändare", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_account_list_an_unelevated_process_does_not_claim_administrator()
    {
        // Fail safe: unable to confirm means "not an administrator", never a
        // guess in the permissive direction.
        var capabilities = WindowsCapabilityAnalyzer.Analyze(SecurityFixtures.Windows11Home());

        Assert.False(capabilities.CurrentUserIsAdministrator);
    }

    [Fact]
    public void The_account_is_matched_by_sid_rather_than_by_name()
    {
        // A different account happens to share the display name; only the SID
        // match may grant administrator.
        var capabilities = WindowsCapabilityAnalyzer.Analyze(
            SecurityFixtures.Windows11Home() with { CurrentUserSid = "S-1-5-21-9-9-9-4242" },
            [SecurityFixtures.Admin("Jimmy")]);

        Assert.False(capabilities.CurrentUserIsAdministrator);
    }
}
