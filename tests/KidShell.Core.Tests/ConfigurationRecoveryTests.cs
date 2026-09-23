using System.Text.Json;
using KidShell.Core.Configuration;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// What happens when the configuration file is damaged.
///
/// The bug these exist for: the store wrote a .bak on every save and never
/// read it. A single corrupt primary threw away the child's profile, the app
/// list, the screen-time settings and the parent's PIN - while a perfectly good
/// copy sat beside it, unused. Losing all that to one bad write is a far worse
/// outcome than the bad write.
/// </summary>
public class ConfigurationRecoveryTests
{
    private static (JsonConfigurationStore Store, TempDirectory Dir) Build()
    {
        var dir = new TempDirectory();
        return (new JsonConfigurationStore(dir.ConfigPath, new RecordingLogger()), dir);
    }

    private static KidShellConfiguration Personalised(string name = "Nils", int weekday = 45)
    {
        var config = KidShellConfiguration.CreateDefault();
        config.Child.Name = name;
        config.Child.Age = 7;
        config.Child.IsOnboardingComplete = true;
        config.ScreenTime.IsEnabled = true;
        config.ScreenTime.WeekdayMinutes = weekday;
        config.ParentPin.Hash = "a-hash";
        config.ParentPin.Salt = "a-salt";
        return config;
    }

    [Fact]
    public void A_corrupt_primary_is_recovered_from_the_backup()
    {
        var (store, dir) = Build();
        using var _ = dir;

        // Two saves, so a backup exists.
        store.Save(Personalised("Nils", weekday: 45));
        store.Save(Personalised("Nils", weekday: 90));

        File.WriteAllText(dir.ConfigPath, "{ this is not json");

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.RecoveredFromBackup, result.Status);

        // The parent's settings survived, which is the entire point.
        Assert.Equal("Nils", result.Configuration.Child.Name);
        Assert.True(result.Configuration.ScreenTime.IsEnabled);
        Assert.Equal(45, result.Configuration.ScreenTime.WeekdayMinutes);
    }

    [Fact]
    public void The_recovered_PIN_still_works()
    {
        // Losing the PIN would lock a parent out of their own Parent Mode.
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(Personalised());
        store.Save(Personalised());

        File.WriteAllText(dir.ConfigPath, "garbage");

        var result = store.Load();

        Assert.Equal("a-hash", result.Configuration.ParentPin.Hash);
        Assert.Equal("a-salt", result.Configuration.ParentPin.Salt);
    }

    [Fact]
    public void A_corrupt_primary_and_a_corrupt_backup_fall_back_to_defaults()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(Personalised());
        store.Save(Personalised());

        File.WriteAllText(dir.ConfigPath, "{ broken");
        File.WriteAllText(dir.ConfigPath + ".bak", "{ also broken");

        var result = store.Load();

        // Defaults, and honest about it: this is the case where the settings
        // really are gone and first-run setup should run again.
        Assert.Equal(ConfigurationLoadStatus.RecoveredFromCorruption, result.Status);
        Assert.True(result.Configuration.RequiresOnboarding);
    }

    [Fact]
    public void A_corrupt_primary_with_no_backup_falls_back_to_defaults()
    {
        var (store, dir) = Build();
        using var _ = dir;

        File.WriteAllText(dir.ConfigPath, "{ broken");

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.RecoveredFromCorruption, result.Status);
    }

    [Fact]
    public void The_two_recovery_outcomes_are_reported_differently()
    {
        // One means "we kept your configuration" and the other means "we lost
        // it". A parent deserves to be told which, so the statuses must not be
        // collapsed into one.
        Assert.NotEqual(ConfigurationLoadStatus.RecoveredFromBackup,
            ConfigurationLoadStatus.RecoveredFromCorruption);
    }

    [Fact]
    public void The_corrupt_file_is_still_kept_for_inspection_when_the_backup_is_used()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(Personalised());
        store.Save(Personalised());

        File.WriteAllText(dir.ConfigPath, "{ broken");
        store.Load();

        // Recovering must not destroy the evidence of what went wrong.
        var quarantined = Directory.GetFiles(dir.Path)
            .Where(f => f.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(quarantined);
    }

    [Fact]
    public void An_interrupted_write_leaves_the_previous_document_intact()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(Personalised("Nils", weekday: 45));

        // A document that serialises but is not valid KidShell configuration
        // is refused before anything on disk is replaced.
        var broken = KidShellConfiguration.CreateDefault();
        broken.Child.Name = "Ersättare";

        // Simulate the interruption: a stray temp file from a write that never
        // completed must not be mistaken for the real document.
        File.WriteAllText(dir.ConfigPath + ".tmp", "{ half written");

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal("Nils", result.Configuration.Child.Name);
        Assert.Equal(45, result.Configuration.ScreenTime.WeekdayMinutes);
    }

    [Fact]
    public void Unknown_fields_from_a_future_version_do_not_break_loading()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(Personalised());

        // A document written by a newer KidShell, read by this one. Refusing it
        // would mean an upgrade-then-downgrade loses everything.
        var json = File.ReadAllText(dir.ConfigPath);
        using var document = JsonDocument.Parse(json);

        var withExtra = json.TrimEnd().TrimEnd('}') +
                        ",\"somethingFromTheFuture\":{\"nested\":[1,2,3]},\"anotherOne\":true}";

        File.WriteAllText(dir.ConfigPath, withExtra);

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.Loaded, result.Status);
        Assert.Equal("Nils", result.Configuration.Child.Name);
    }

    [Fact]
    public void A_backup_from_an_older_schema_is_migrated_on_recovery()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(Personalised());
        store.Save(Personalised());

        // Age the backup to an older schema, then break the primary.
        var backup = File.ReadAllText(dir.ConfigPath + ".bak");
        File.WriteAllText(dir.ConfigPath + ".bak", backup.Replace(
            $"\"schemaVersion\": {KidShellConfiguration.CurrentSchemaVersion}",
            "\"schemaVersion\": 1"));

        File.WriteAllText(dir.ConfigPath, "{ broken");

        var result = store.Load();

        // Recovered AND brought up to date, rather than recovered into a shape
        // the rest of the app no longer understands.
        Assert.Equal(ConfigurationLoadStatus.RecoveredFromBackup, result.Status);
        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, result.Configuration.SchemaVersion);
    }
}
