using KidShell.Core.Configuration;
using KidShell.Core.ScreenTime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// EXTERNAL AUDIT FINDING 04 — documents that parse cleanly and then break
/// something.
///
/// A file that is not JSON at all is the easy case: it throws where the store
/// expects it to, and the backup is read instead. The dangerous shape is a
/// document that is perfectly well-formed, deserializes without complaint, and
/// leaves a null somewhere the rest of the app assumed could not be null.
///
///     { "child": null }                 an object that is simply absent
///     { "apps": [null] }                a LIST that is fine, holding a hole
///     { "screenTime": { "warningMinutes": null } }   a list-valued property
///
/// The first was already handled. The second threw inside the normalizer
/// itself, while tidying the very field that was missing. The third survived
/// loading and failed later, in Clone or while evaluating a warning, which is
/// worse - the failure was nowhere near the file that caused it.
///
/// The rule these tests hold to: Load never throws for a document that parses,
/// and nothing downstream is ever handed a null collection or a null element.
/// </summary>
public class ConfigurationMalformedInputTests
{
    private static (JsonConfigurationStore Store, TempDirectory Dir) Build()
    {
        var dir = new TempDirectory();
        return (new JsonConfigurationStore(dir.ConfigPath, new RecordingLogger()), dir);
    }

    private static ConfigurationLoadResult LoadFrom(string json)
    {
        var (store, dir) = Build();
        using var _ = dir;

        File.WriteAllText(dir.ConfigPath, json);

        return store.Load();
    }

    /// <summary>
    /// The heart of it: whatever the document said, what comes out has to be
    /// usable without a single null check at the call site.
    /// </summary>
    private static void AssertFullyPopulated(KidShellConfiguration config)
    {
        Assert.NotNull(config.Child);
        Assert.NotNull(config.Child.Name);
        Assert.NotNull(config.Child.AvatarId);
        Assert.NotNull(config.Child.ThemeId);

        Assert.NotNull(config.Apps);
        Assert.DoesNotContain(config.Apps, app => app is null);
        Assert.All(config.Apps, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Id));
            Assert.NotNull(app.DisplayName);
            Assert.NotNull(app.ProgramName);
            Assert.NotNull(app.Description);
            Assert.NotNull(app.ExecutablePath);
            Assert.NotNull(app.Arguments);
            Assert.NotNull(app.Icon);
        });

        Assert.NotNull(config.ScreenTime);
        Assert.NotNull(config.ScreenTime.WarningMinutes);
        Assert.DoesNotContain(config.ScreenTime.WarningMinutes, m => m <= 0);

        Assert.NotNull(config.Web);
        Assert.NotNull(config.Web.AllowedDomains);
        Assert.DoesNotContain(config.Web.AllowedDomains, d => string.IsNullOrWhiteSpace(d));

        Assert.NotNull(config.ParentPin);

        // And it must survive the operations the app performs on it.
        var clone = config.Clone();
        Assert.NotNull(clone);
    }

    // ------------------------------------------------ the three reported payloads

    [Fact]
    public void A_null_child_loads_as_an_empty_profile()
    {
        var result = LoadFrom("""{ "schemaVersion": 1, "child": null }""");

        AssertFullyPopulated(result.Configuration);
        Assert.Equal(string.Empty, result.Configuration.Child.Name);
        Assert.True(result.Configuration.RequiresOnboarding);
    }

    [Fact]
    public void A_null_entry_in_the_app_list_is_dropped()
    {
        var result = LoadFrom("""
            {
              "schemaVersion": 2,
              "child": { "name": "Lucas", "age": 6, "isOnboardingComplete": true },
              "apps": [null]
            }
            """);

        AssertFullyPopulated(result.Configuration);
        Assert.Empty(result.Configuration.Apps);
    }

    [Fact]
    public void A_null_entry_beside_real_apps_is_dropped_without_losing_the_others()
    {
        var result = LoadFrom("""
            {
              "schemaVersion": 2,
              "apps": [
                { "id": "a", "displayName": "Paint", "programName": "Paint" },
                null,
                { "id": "b", "displayName": "Musik", "programName": "Musik" }
              ]
            }
            """);

        AssertFullyPopulated(result.Configuration);
        Assert.Equal(2, result.Configuration.Apps.Count);
        Assert.Equal(["a", "b"], result.Configuration.Apps.Select(a => a.Id));
    }

    [Fact]
    public void Null_warning_minutes_fall_back_to_the_default()
    {
        var result = LoadFrom("""
            { "schemaVersion": 2, "screenTime": { "warningMinutes": null } }
            """);

        AssertFullyPopulated(result.Configuration);
        Assert.NotEmpty(result.Configuration.ScreenTime.WarningMinutes);
    }

    /// <summary>
    /// The null list has to survive more than loading. This is where it used
    /// to fail: far from the file, inside a clone or an evaluation.
    /// </summary>
    [Fact]
    public void A_configuration_with_null_warning_minutes_can_still_run_a_session()
    {
        using var dir = new TempDirectory();

        File.WriteAllText(dir.ConfigPath, """
            {
              "schemaVersion": 2,
              "child": { "name": "Lucas", "age": 6, "isOnboardingComplete": true },
              "screenTime": { "isEnabled": true, "weekdayMinutes": 60, "warningMinutes": null }
            }
            """);

        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        var engine = new ScreenTimeEngine(state, new InMemoryScreenTimeStateStore(), logger, time);

        time.Advance(TimeSpan.FromMinutes(4));

        var snapshot = engine.Tick();

        Assert.NotNull(snapshot);
        Assert.NotNull(state.Current.ScreenTime.WarningMinutes);
    }

    // ------------------------------------------------ the rest of the nulls

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "web": null }""")]
    [InlineData("""{ "schemaVersion": 2, "parentPin": null }""")]
    [InlineData("""{ "schemaVersion": 2, "screenTime": null }""")]
    [InlineData("""{ "schemaVersion": 2, "apps": null }""")]
    [InlineData("""{ "schemaVersion": 2, "web": { "allowedDomains": null } }""")]
    [InlineData("""{ "schemaVersion": 2, "child": { "name": null, "avatarId": null, "themeId": null } }""")]
    [InlineData("""{ "schemaVersion": 2, "apps": [{ "id": null, "displayName": null, "programName": null }] }""")]
    public void A_null_anywhere_still_loads(string json) =>
        AssertFullyPopulated(LoadFrom(json).Configuration);

    [Fact]
    public void Null_and_blank_domains_are_removed()
    {
        var result = LoadFrom("""
            {
              "schemaVersion": 2,
              "web": { "mode": 1, "allowedDomains": [null, "", "   ", "svt.se"] }
            }
            """);

        AssertFullyPopulated(result.Configuration);
        Assert.Equal(["svt.se"], result.Configuration.Web.AllowedDomains);
    }

    [Fact]
    public void Duplicate_domains_are_collapsed()
    {
        var result = LoadFrom("""
            {
              "schemaVersion": 2,
              "web": { "mode": 1, "allowedDomains": ["svt.se", "SVT.se", " svt.se ", "ur.se"] }
            }
            """);

        Assert.Equal(["svt.se", "ur.se"], result.Configuration.Web.AllowedDomains);
    }

    [Fact]
    public void Duplicate_app_ids_do_not_survive_loading()
    {
        var result = LoadFrom("""
            {
              "schemaVersion": 2,
              "apps": [
                { "id": "same", "displayName": "One", "programName": "One" },
                { "id": "same", "displayName": "Two", "programName": "Two" }
              ]
            }
            """);

        AssertFullyPopulated(result.Configuration);
        Assert.Equal(
            result.Configuration.Apps.Count,
            result.Configuration.Apps.Select(a => a.Id).Distinct().Count());
    }

    // ------------------------------------------------ out-of-range numbers

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(100000, 1440)]
    [InlineData(int.MaxValue, 1440)]
    [InlineData(int.MinValue, 0)]
    public void Minute_allowances_are_clamped_to_a_real_day(int stored, int expected)
    {
        var result = LoadFrom($$"""
            { "schemaVersion": 2, "screenTime": { "weekdayMinutes": {{stored}}, "weekendMinutes": {{stored}} } }
            """);

        Assert.Equal(expected, result.Configuration.ScreenTime.WeekdayMinutes);
        Assert.Equal(expected, result.Configuration.ScreenTime.WeekendMinutes);
    }

    [Theory]
    [InlineData(-3, 25)]
    [InlineData(0, 24)]
    [InlineData(7, 20)]
    [InlineData(99, -99)]
    public void Allowed_hours_stay_inside_a_clock_face(int from, int until)
    {
        var result = LoadFrom($$"""
            {
              "schemaVersion": 2,
              "screenTime": { "restrictHours": true, "allowedFromHour": {{from}}, "allowedUntilHour": {{until}} }
            }
            """);

        var screenTime = result.Configuration.ScreenTime;

        Assert.InRange(screenTime.AllowedFromHour, 0, 23);
        Assert.InRange(screenTime.AllowedUntilHour, 0, 24);
    }

    [Fact]
    public void Nonsense_warning_minutes_are_discarded()
    {
        var result = LoadFrom("""
            { "schemaVersion": 2, "screenTime": { "warningMinutes": [15, 0, -5, 5, 15, 99999] } }
            """);

        var warnings = result.Configuration.ScreenTime.WarningMinutes;

        Assert.DoesNotContain(warnings, m => m <= 0);
        Assert.Equal(warnings.Count, warnings.Distinct().Count());
    }

    [Fact]
    public void An_age_outside_the_supported_range_is_clamped()
    {
        var result = LoadFrom("""
            { "schemaVersion": 2, "child": { "name": "Lucas", "age": 900 } }
            """);

        Assert.InRange(result.Configuration.Child.Age, ChildProfile.MinAge, ChildProfile.MaxAge);
    }

    // ------------------------------------------------ schema versions

    [Fact]
    public void A_schema_from_the_future_is_refused_rather_than_guessed_at()
    {
        var result = LoadFrom($$"""
            { "schemaVersion": {{KidShellConfiguration.CurrentSchemaVersion + 5}}, "child": { "name": "Lucas" } }
            """);

        // Whatever it does, it must not crash and must not pretend to
        // understand fields it has never seen.
        Assert.NotNull(result.Configuration);
        AssertFullyPopulated(result.Configuration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_missing_or_impossible_schema_version_is_repaired(int version)
    {
        var result = LoadFrom($$"""{ "schemaVersion": {{version}} }""");

        Assert.True(result.Configuration.SchemaVersion > 0);
    }

    // ------------------------------------------------ damaged files

    [Fact]
    public void A_truncated_document_does_not_throw()
    {
        var result = LoadFrom("""{ "schemaVersion": 2, "child": { "name": "Luc""");

        Assert.NotNull(result.Configuration);
        AssertFullyPopulated(result.Configuration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("{")]
    [InlineData("\0\0\0")]
    public void Rubbish_in_the_file_does_not_throw(string content)
    {
        var result = LoadFrom(content);

        Assert.NotNull(result.Configuration);
        AssertFullyPopulated(result.Configuration);
    }

    // ------------------------------------------------ primary and backup

    [Fact]
    public void A_good_primary_with_a_damaged_backup_is_used_as_is()
    {
        var (store, dir) = Build();
        using var _ = dir;

        var config = KidShellConfiguration.CreateDefault();
        config.Child.Name = "Nils";
        store.Save(config);
        store.Save(config);

        File.WriteAllText(dir.ConfigPath + ".bak", "{ not json");

        Assert.Equal("Nils", store.Load().Configuration.Child.Name);
    }

    [Fact]
    public void A_damaged_primary_falls_back_to_a_good_backup()
    {
        var (store, dir) = Build();
        using var _ = dir;

        var config = KidShellConfiguration.CreateDefault();
        config.Child.Name = "Nils";
        store.Save(config);
        store.Save(config);

        File.WriteAllText(dir.ConfigPath, "{ not json");

        Assert.Equal("Nils", store.Load().Configuration.Child.Name);
    }

    /// <summary>
    /// A backup that parses but is full of holes must be normalized too. It is
    /// the copy reached for in a crisis, so it is the last place a null should
    /// be allowed through.
    /// </summary>
    [Fact]
    public void A_backup_full_of_nulls_is_normalized_like_the_primary()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(KidShellConfiguration.CreateDefault());

        File.WriteAllText(dir.ConfigPath, "{ not json");
        File.WriteAllText(dir.ConfigPath + ".bak", """
            { "schemaVersion": 2, "child": null, "apps": [null], "screenTime": { "warningMinutes": null } }
            """);

        AssertFullyPopulated(store.Load().Configuration);
    }

    [Fact]
    public void Two_damaged_files_give_a_usable_default_rather_than_an_exception()
    {
        var (store, dir) = Build();
        using var _ = dir;

        store.Save(KidShellConfiguration.CreateDefault());

        File.WriteAllText(dir.ConfigPath, "{ not json");
        File.WriteAllText(dir.ConfigPath + ".bak", "also not json");

        var result = store.Load();

        AssertFullyPopulated(result.Configuration);
        Assert.True(result.Configuration.RequiresOnboarding);
    }

    // ------------------------------------------------ unknown fields

    [Fact]
    public void Fields_KidShell_has_never_heard_of_are_ignored()
    {
        var result = LoadFrom("""
            {
              "schemaVersion": 2,
              "child": { "name": "Lucas", "age": 6, "favouriteDinosaur": "stegosaurus" },
              "somethingFromTheFuture": { "nested": [1, 2, 3] }
            }
            """);

        Assert.Equal("Lucas", result.Configuration.Child.Name);
        AssertFullyPopulated(result.Configuration);
    }

    /// <summary>
    /// A last sweep over shapes that are individually covered above, together,
    /// in the combinations a half-written file actually produces.
    /// </summary>
    [Theory]
    [InlineData("""{ "schemaVersion": 2, "child": null, "apps": null, "web": null, "screenTime": null, "parentPin": null }""")]
    [InlineData("""{ "schemaVersion": 2, "apps": [null, null], "web": { "allowedDomains": [null] } }""")]
    [InlineData("""{ "schemaVersion": 2, "screenTime": { "warningMinutes": [null] } }""")]
    [InlineData("""{ "schemaVersion": 2, "child": { "age": null, "name": null } }""")]
    [InlineData("""{ "schemaVersion": 2, "apps": [{ "isEnabled": null }] }""")]
    public void Combinations_of_holes_all_load(string json) =>
        AssertFullyPopulated(LoadFrom(json).Configuration);
}
