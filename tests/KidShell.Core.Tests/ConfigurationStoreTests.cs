using System.Text.Json;
using KidShell.Core.Configuration;
using Xunit;

namespace KidShell.Core.Tests;

public class ConfigurationStoreTests
{
    [Fact]
    public void Loading_a_missing_file_creates_defaults_without_throwing()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.CreatedDefaults, result.Status);
        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, result.Configuration.SchemaVersion);
        Assert.False(File.Exists(dir.ConfigPath));
    }

    [Fact]
    public void Saved_configuration_round_trips_every_section()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        var config = KidShellConfiguration.CreateDefault();
        config.Child.Name = "Nora";
        config.Child.Age = 8;
        config.Child.AvatarId = "owl";
        config.Child.ThemeId = ThemeIds.Space;
        config.ScreenTime.IsEnabled = true;
        config.ScreenTime.WeekdayMinutes = 45;
        config.ScreenTime.WeekendMinutes = 150;
        config.Web.Mode = WebMode.Allowlist;
        config.Web.AllowedDomains = ["svt.se"];
        config.FindApp("minecraft")!.IsEnabled = false;

        Assert.True(store.Save(config));

        var loaded = store.Load().Configuration;

        Assert.Equal("Nora", loaded.Child.Name);
        Assert.Equal(8, loaded.Child.Age);
        Assert.Equal("owl", loaded.Child.AvatarId);
        Assert.Equal(ThemeIds.Space, loaded.Child.ThemeId);
        Assert.True(loaded.ScreenTime.IsEnabled);
        Assert.Equal(45, loaded.ScreenTime.WeekdayMinutes);
        Assert.Equal(150, loaded.ScreenTime.WeekendMinutes);
        Assert.Equal(WebMode.Allowlist, loaded.Web.Mode);
        Assert.Equal(["svt.se"], loaded.Web.AllowedDomains);
        Assert.False(loaded.FindApp("minecraft")!.IsEnabled);
    }

    [Fact]
    public void Saved_document_records_the_schema_version()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        store.Save(KidShellConfiguration.CreateDefault());

        using var document = JsonDocument.Parse(File.ReadAllText(dir.ConfigPath));

        Assert.Equal(
            KidShellConfiguration.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Derived_properties_are_not_persisted()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        store.Save(KidShellConfiguration.CreateDefault());

        using var document = JsonDocument.Parse(File.ReadAllText(dir.ConfigPath));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("enabledApps", out _));
        Assert.False(root.GetProperty("apps")[0].TryGetProperty("effectiveProgramName", out _));
    }

    [Fact]
    public void Second_save_keeps_the_previous_document_as_a_backup()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        var first = KidShellConfiguration.CreateDefault();
        first.Child.Name = "First";
        store.Save(first);

        var second = KidShellConfiguration.CreateDefault();
        second.Child.Name = "Second";
        store.Save(second);

        Assert.True(File.Exists(dir.ConfigPath + ".bak"));
        Assert.Contains("First", File.ReadAllText(dir.ConfigPath + ".bak"));
        Assert.Contains("Second", File.ReadAllText(dir.ConfigPath));
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        store.Save(KidShellConfiguration.CreateDefault());
        store.Save(KidShellConfiguration.CreateDefault());

        Assert.False(File.Exists(dir.ConfigPath + ".tmp"));
    }

    [Fact]
    public void A_corrupt_document_falls_back_to_defaults_and_is_logged()
    {
        using var dir = new TempDirectory();
        var (_, store, logger) = TestFactory.CreateState(dir);

        File.WriteAllText(dir.ConfigPath, "{ this is not json ");

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.RecoveredFromCorruption, result.Status);
        Assert.Equal(string.Empty, result.Configuration.Child.Name);
        Assert.True(result.Configuration.RequiresOnboarding);
        Assert.True(logger.HasError);
    }

    [Fact]
    public void A_corrupt_document_is_copied_aside_for_inspection()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        File.WriteAllText(dir.ConfigPath, "not json at all");
        store.Load();

        var quarantined = Directory.GetFiles(dir.Path, "*.corrupt-*");

        Assert.Single(quarantined);
        Assert.Equal("not json at all", File.ReadAllText(quarantined[0]));
    }

    [Fact]
    public void A_truncated_but_parseable_document_is_repaired_rather_than_rejected()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);

        // Valid JSON, but missing almost everything the app expects.
        File.WriteAllText(dir.ConfigPath, """{ "schemaVersion": 0 }""");

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, result.Configuration.SchemaVersion);
        Assert.NotNull(result.Configuration.Child);
        Assert.NotNull(result.Configuration.Apps);
        Assert.NotNull(result.Configuration.Web.AllowedDomains);
        Assert.NotNull(result.Configuration.ParentPin);
    }

    [Fact]
    public void Normalize_clamps_nonsense_values()
    {
        var config = new KidShellConfiguration
        {
            SchemaVersion = -4,
            Child = new ChildProfile { Name = "  Nora  ", Age = 99 },
            ScreenTime = new ScreenTimeSettings { WeekdayMinutes = -30, WeekendMinutes = 99_999 }
        };

        JsonConfigurationStore.Normalize(config);

        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal(ChildProfile.MaxAge, config.Child.Age);
        Assert.Equal("Nora", config.Child.Name);
        Assert.Equal(0, config.ScreenTime.WeekdayMinutes);
        Assert.Equal(24 * 60, config.ScreenTime.WeekendMinutes);
    }

    [Fact]
    public void Normalize_never_invents_a_child()
    {
        // A blank profile is a legitimate state: it means setup has not run.
        var config = new KidShellConfiguration
        {
            Child = new ChildProfile { Name = "   ", Age = 0 }
        };

        JsonConfigurationStore.Normalize(config);

        Assert.Equal(string.Empty, config.Child.Name);
        Assert.Equal(0, config.Child.Age);
        Assert.True(config.RequiresOnboarding);
    }

    [Fact]
    public void Normalize_truncates_an_absurdly_long_name()
    {
        var config = new KidShellConfiguration
        {
            Child = new ChildProfile { Name = new string('a', 500), Age = 7 }
        };

        JsonConfigurationStore.Normalize(config);

        Assert.Equal(ChildProfile.MaxNameLength, config.Child.Name.Length);
    }

    [Fact]
    public void Normalize_gives_an_id_to_an_app_that_has_none()
    {
        var config = new KidShellConfiguration
        {
            Apps = [new KidAppDefinition { Id = "", DisplayName = "Utan id" }]
        };

        JsonConfigurationStore.Normalize(config);

        Assert.False(string.IsNullOrWhiteSpace(config.Apps[0].Id));
    }
}
