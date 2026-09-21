using KidShell.Core.Configuration;
using Xunit;

namespace KidShell.Core.Tests;

public class ConfigurationDefaultsTests
{
    [Fact]
    public void Default_configuration_declares_schema_version_2()
    {
        var config = KidShellConfiguration.CreateDefault();

        Assert.Equal(2, KidShellConfiguration.CurrentSchemaVersion);
        Assert.Equal(2, config.SchemaVersion);
    }

    [Fact]
    public void Default_configuration_ships_no_child_at_all()
    {
        // KidShell must never pretend a child has already been set up. A
        // placeholder profile on first launch was the MVP 0.1 bug this
        // asserts against.
        var config = KidShellConfiguration.CreateDefault();

        Assert.Equal(string.Empty, config.Child.Name);
        Assert.Equal(0, config.Child.Age);
        Assert.Equal(string.Empty, config.Child.AvatarId);
        Assert.False(config.Child.HasRequiredDetails);
    }

    [Fact]
    public void Default_configuration_requires_onboarding()
    {
        var config = KidShellConfiguration.CreateDefault();

        Assert.False(config.Child.IsOnboardingComplete);
        Assert.True(config.RequiresOnboarding);
    }

    [Fact]
    public void Default_configuration_still_has_a_usable_theme()
    {
        var config = KidShellConfiguration.CreateDefault();

        Assert.True(ThemeIds.IsKnown(config.Child.ThemeId));
    }

    [Fact]
    public void Default_configuration_enables_the_eight_cards_from_the_child_design()
    {
        var config = KidShellConfiguration.CreateDefault();

        var enabled = config.EnabledApps.Select(a => a.DisplayName).ToArray();

        Assert.Equal(
            ["Rita", "Spel", "Lära", "Miniräknare", "Musik", "Läsa", "Film", "Minecraft"],
            enabled);
    }

    [Fact]
    public void Default_configuration_includes_the_parent_only_rows_switched_off()
    {
        var config = KidShellConfiguration.CreateDefault();

        var vlc = config.FindApp("vlc");
        var browser = config.FindApp("browser");

        Assert.NotNull(vlc);
        Assert.NotNull(browser);
        Assert.False(vlc!.IsEnabled);
        Assert.False(browser!.IsEnabled);
    }

    [Fact]
    public void Paint_and_calculator_are_pointed_at_the_windows_programs()
    {
        var config = KidShellConfiguration.CreateDefault();

        Assert.Equal("mspaint.exe", config.FindApp("paint")?.ExecutablePath);
        Assert.Equal("calc.exe", config.FindApp("calculator")?.ExecutablePath);
    }

    [Fact]
    public void Enabled_apps_are_returned_in_sort_order()
    {
        var config = KidShellConfiguration.CreateDefault();
        config.FindApp("paint")!.SortOrder = 99;

        var order = config.EnabledApps.Select(a => a.Id).ToArray();

        Assert.Equal("paint", order[^1]);
    }

    [Fact]
    public void Default_configuration_has_no_stored_parent_pin()
    {
        // MVP 0.1 falls back to the development PIN; nothing is written to disk
        // until a parent chooses their own.
        var config = KidShellConfiguration.CreateDefault();

        Assert.False(config.ParentPin.IsConfigured);
        Assert.Null(config.ParentPin.Hash);
        Assert.Null(config.ParentPin.Salt);
    }

    [Fact]
    public void Clone_is_deep_so_a_parent_draft_cannot_leak_into_child_mode()
    {
        var config = KidShellConfiguration.CreateDefault();
        var draft = config.Clone();

        draft.Child.Name = "Nora";
        draft.Child.IsOnboardingComplete = true;
        draft.FindApp("paint")!.IsEnabled = false;
        draft.Web.AllowedDomains.Add("example.com");
        draft.ScreenTime.WeekdayMinutes = 15;

        Assert.Equal(string.Empty, config.Child.Name);
        Assert.False(config.Child.IsOnboardingComplete);
        Assert.True(config.FindApp("paint")!.IsEnabled);
        Assert.DoesNotContain("example.com", config.Web.AllowedDomains);
        Assert.Equal(60, config.ScreenTime.WeekdayMinutes);
    }

    [Fact]
    public void Program_name_falls_back_to_the_child_facing_name()
    {
        var withProgramName = new KidAppDefinition { DisplayName = "Rita", ProgramName = "Paint" };
        var withoutProgramName = new KidAppDefinition { DisplayName = "Musik" };

        Assert.Equal("Paint", withProgramName.EffectiveProgramName);
        Assert.Equal("Musik", withoutProgramName.EffectiveProgramName);
    }
}
