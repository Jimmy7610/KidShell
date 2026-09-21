using KidShell.Core.Configuration;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Schema 1 → 2. The rule under test is: throw away the shipped placeholder
/// profile, keep everything a parent actually chose.
/// </summary>
public class ConfigurationMigrationTests
{
    /// <summary>
    /// A schema 1 document as MVP 0.1 wrote it, including the placeholder
    /// child that this milestone exists to remove.
    /// </summary>
    private const string SchemaOnePlaceholderDocument = """
    {
      "schemaVersion": 1,
      "child": { "name": "Alice", "age": 6, "avatarId": "fox", "themeId": "meadow" },
      "apps": [
        { "id": "paint", "displayName": "Rita", "icon": "paint", "accentStyle": "Rose",
          "isEnabled": true, "executablePath": "mspaint.exe", "sortOrder": 0 },
        { "id": "vlc", "displayName": "VLC", "icon": "video", "accentStyle": "Sun",
          "isEnabled": false, "executablePath": "", "sortOrder": 1 }
      ],
      "screenTime": { "isEnabled": true, "weekdayMinutes": 45, "weekendMinutes": 90 },
      "web": { "mode": "Allowlist", "allowedDomains": [ "svt.se" ] },
      "parentPin": { "hash": "aGFzaA==", "salt": "c2FsdA==", "iterations": 210000 }
    }
    """;

    /// <summary>The same vintage, but personalised by a real parent.</summary>
    private const string SchemaOnePersonalisedDocument = """
    {
      "schemaVersion": 1,
      "child": { "name": "Nora", "age": 8, "avatarId": "owl", "themeId": "sunset" },
      "apps": [
        { "id": "paint", "displayName": "Rita", "icon": "paint", "accentStyle": "Rose",
          "isEnabled": true, "executablePath": "mspaint.exe", "sortOrder": 0 }
      ],
      "screenTime": { "isEnabled": false, "weekdayMinutes": 60, "weekendMinutes": 120 },
      "web": { "mode": "NoBrowser", "allowedDomains": [] },
      "parentPin": { "iterations": 210000 }
    }
    """;

    [Fact]
    public void A_schema_1_placeholder_profile_is_discarded_and_setup_runs()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);
        File.WriteAllText(dir.ConfigPath, SchemaOnePlaceholderDocument);

        var config = store.Load().Configuration;

        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal(string.Empty, config.Child.Name);
        Assert.Equal(0, config.Child.Age);
        Assert.Equal(string.Empty, config.Child.AvatarId);
        Assert.True(config.RequiresOnboarding);
    }

    [Fact]
    public void Discarding_the_placeholder_keeps_every_other_setting()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);
        File.WriteAllText(dir.ConfigPath, SchemaOnePlaceholderDocument);

        var config = store.Load().Configuration;

        Assert.Equal(2, config.Apps.Count);
        Assert.True(config.FindApp("paint")!.IsEnabled);
        Assert.False(config.FindApp("vlc")!.IsEnabled);
        Assert.True(config.ScreenTime.IsEnabled);
        Assert.Equal(45, config.ScreenTime.WeekdayMinutes);
        Assert.Equal(WebMode.Allowlist, config.Web.Mode);
        Assert.Contains("svt.se", config.Web.AllowedDomains);
        Assert.True(config.ParentPin.IsConfigured);
    }

    [Fact]
    public void A_schema_1_profile_a_parent_personalised_is_carried_over()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);
        File.WriteAllText(dir.ConfigPath, SchemaOnePersonalisedDocument);

        var config = store.Load().Configuration;

        // Upgrading must never push a family back through setup.
        Assert.Equal("Nora", config.Child.Name);
        Assert.Equal(8, config.Child.Age);
        Assert.Equal("owl", config.Child.AvatarId);
        Assert.True(config.Child.IsOnboardingComplete);
        Assert.False(config.RequiresOnboarding);
    }

    [Fact]
    public void Legacy_theme_ids_are_translated_rather_than_dropped()
    {
        Assert.Equal(ThemeIds.Forest, ThemeIds.Migrate("meadow"));
        Assert.Equal(ThemeIds.Bright, ThemeIds.Migrate("sunset"));
        Assert.Equal(ThemeIds.Ocean, ThemeIds.Migrate("ocean"));
        Assert.Equal(ThemeIds.Default, ThemeIds.Migrate("something-else"));
        Assert.Equal(ThemeIds.Default, ThemeIds.Migrate(null));
    }

    [Fact]
    public void A_migrated_document_carries_its_theme_across()
    {
        using var dir = new TempDirectory();
        var (_, store, _) = TestFactory.CreateState(dir);
        File.WriteAllText(dir.ConfigPath, SchemaOnePersonalisedDocument);

        var config = store.Load().Configuration;

        Assert.Equal(ThemeIds.Bright, config.Child.ThemeId);
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);
        File.WriteAllText(dir.ConfigPath, SchemaOnePersonalisedDocument);

        state.Initialize();
        var first = state.Current.Clone();

        // Load it again from what was written back.
        var (second, _, _) = TestFactory.CreateState(dir);
        second.Initialize();

        Assert.True(ConfigurationSnapshot.AreEquivalent(first, second.Current));
        Assert.Equal("Nora", second.Current.Child.Name);
        Assert.False(second.Current.RequiresOnboarding);
    }

    [Fact]
    public void Migrate_reports_whether_it_changed_anything()
    {
        var legacy = new KidShellConfiguration { SchemaVersion = 1 };
        Assert.True(ConfigurationMigrator.Migrate(legacy));
        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, legacy.SchemaVersion);

        var current = KidShellConfiguration.CreateDefault();
        Assert.False(ConfigurationMigrator.Migrate(current));
    }

    [Fact]
    public void A_newer_document_is_left_alone_rather_than_downgraded()
    {
        var future = new KidShellConfiguration { SchemaVersion = 99 };

        Assert.False(ConfigurationMigrator.Migrate(future));
        Assert.Equal(99, future.SchemaVersion);
    }

    [Fact]
    public void A_migrated_document_is_written_back_at_the_current_schema()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.ConfigPath, SchemaOnePlaceholderDocument);

        var (state, _, _) = TestFactory.CreateState(dir);
        state.Initialize();

        // The old document must not linger on disk: the next start should be
        // an ordinary load, and the placeholder name should be gone for good.
        var onDisk = File.ReadAllText(dir.ConfigPath);

        Assert.Contains("\"schemaVersion\": 2", onDisk);
        Assert.DoesNotContain("Alice", onDisk);
    }

    [Fact]
    public void An_existing_install_is_still_loadable_after_the_schema_bump()
    {
        using var dir = new TempDirectory();
        var (_, store, logger) = TestFactory.CreateState(dir);
        File.WriteAllText(dir.ConfigPath, SchemaOnePlaceholderDocument);

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.False(logger.HasError);
    }
}
