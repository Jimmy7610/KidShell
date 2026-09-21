using KidShell.Core.Configuration;
using Xunit;

namespace KidShell.Core.Tests;

public class AppStateServiceTests
{
    [Fact]
    public void Initialize_on_a_fresh_machine_writes_the_defaults_out()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);

        var status = state.Initialize();

        Assert.Equal(ConfigurationLoadStatus.CreatedDefaults, status);
        Assert.True(File.Exists(dir.ConfigPath));
    }

    [Fact]
    public void Editing_a_draft_does_not_touch_live_state_until_commit()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.FindApp("minecraft")!.IsEnabled = false;
        draft.Child.Name = "Nora";

        Assert.True(state.Current.FindApp("minecraft")!.IsEnabled);
        Assert.Equal(string.Empty, state.Current.Child.Name);

        Assert.True(state.Commit(draft));

        Assert.False(state.Current.FindApp("minecraft")!.IsEnabled);
        Assert.Equal("Nora", state.Current.Child.Name);
    }

    [Fact]
    public void Commit_raises_configuration_changed_so_child_mode_can_rebuild()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);
        state.Initialize();

        var raised = 0;
        KidShellConfiguration? seen = null;
        state.ConfigurationChanged += (_, e) =>
        {
            raised++;
            seen = e.Configuration;
        };

        var draft = state.CreateDraft();
        draft.Child.Name = "Nora";
        state.Commit(draft);

        Assert.Equal(1, raised);
        Assert.Equal("Nora", seen?.Child.Name);
    }

    [Fact]
    public void Disabling_an_app_removes_it_from_what_the_child_sees()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);
        state.Initialize();

        var before = state.Current.EnabledApps.Count();

        var draft = state.CreateDraft();
        draft.FindApp("minecraft")!.IsEnabled = false;
        state.Commit(draft);

        Assert.Equal(before - 1, state.Current.EnabledApps.Count());
        Assert.DoesNotContain(state.Current.EnabledApps, a => a.Id == "minecraft");
    }

    [Fact]
    public void Enabling_a_parent_only_app_adds_it_to_what_the_child_sees()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.FindApp("vlc")!.IsEnabled = true;
        state.Commit(draft);

        Assert.Contains(state.Current.EnabledApps, a => a.Id == "vlc");
    }

    [Fact]
    public void An_added_app_survives_a_restart()
    {
        using var dir = new TempDirectory();

        var (first, _, _) = TestFactory.CreateState(dir);
        first.Initialize();

        var draft = first.CreateDraft();
        draft.Apps.Add(new KidAppDefinition
        {
            Id = "notes",
            DisplayName = "Anteckna",
            ProgramName = "Anteckningar",
            ExecutablePath = "notepad.exe",
            SortOrder = 50
        });
        Assert.True(first.Commit(draft));

        // A second service over the same directory is exactly what a restart is.
        var (second, _, _) = TestFactory.CreateState(dir);
        Assert.Equal(ConfigurationLoadStatus.Loaded, second.Initialize());

        var restored = second.Current.FindApp("notes");
        Assert.NotNull(restored);
        Assert.Equal("Anteckningar", restored!.EffectiveProgramName);
        Assert.Equal("notepad.exe", restored.ExecutablePath);
    }

    [Fact]
    public void A_removed_app_stays_removed_after_a_restart()
    {
        using var dir = new TempDirectory();

        var (first, _, _) = TestFactory.CreateState(dir);
        first.Initialize();

        var draft = first.CreateDraft();
        draft.Apps.RemoveAll(a => a.Id == "vlc");
        first.Commit(draft);

        var (second, _, _) = TestFactory.CreateState(dir);
        second.Initialize();

        Assert.Null(second.Current.FindApp("vlc"));
    }

    [Fact]
    public void Child_profile_changes_survive_a_restart()
    {
        using var dir = new TempDirectory();

        var (first, _, _) = TestFactory.CreateState(dir);
        first.Initialize();

        var draft = first.CreateDraft();
        draft.Child.Name = "Nora";
        draft.Child.Age = 8;
        draft.Child.AvatarId = "owl";
        draft.Child.ThemeId = ThemeIds.Space;
        first.Commit(draft);

        var (second, _, _) = TestFactory.CreateState(dir);
        second.Initialize();

        Assert.Equal("Nora", second.Current.Child.Name);
        Assert.Equal(8, second.Current.Child.Age);
        Assert.Equal("owl", second.Current.Child.AvatarId);
        Assert.Equal(ThemeIds.Space, second.Current.Child.ThemeId);
    }

    [Fact]
    public void Web_mode_and_allowlist_survive_a_restart()
    {
        using var dir = new TempDirectory();

        var (first, _, _) = TestFactory.CreateState(dir);
        first.Initialize();

        var draft = first.CreateDraft();
        draft.Web.Mode = WebMode.Allowlist;
        draft.Web.AllowedDomains.Add("naturskyddsforeningen.se");
        first.Commit(draft);

        var (second, _, _) = TestFactory.CreateState(dir);
        second.Initialize();

        Assert.Equal(WebMode.Allowlist, second.Current.Web.Mode);
        Assert.Contains("naturskyddsforeningen.se", second.Current.Web.AllowedDomains);
    }

    [Fact]
    public void Screen_time_configuration_survives_a_restart()
    {
        using var dir = new TempDirectory();

        var (first, _, _) = TestFactory.CreateState(dir);
        first.Initialize();

        var draft = first.CreateDraft();
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 45;
        draft.ScreenTime.WeekendMinutes = 90;
        first.Commit(draft);

        var (second, _, _) = TestFactory.CreateState(dir);
        second.Initialize();

        Assert.True(second.Current.ScreenTime.IsEnabled);
        Assert.Equal(45, second.Current.ScreenTime.WeekdayMinutes);
        Assert.Equal(90, second.Current.ScreenTime.WeekendMinutes);
    }

    [Fact]
    public void A_corrupt_file_is_reported_so_the_app_can_tell_the_parent()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.ConfigPath, "}{ broken");

        var (state, _, _) = TestFactory.CreateState(dir);
        var status = state.Initialize();

        Assert.Equal(ConfigurationLoadStatus.RecoveredFromCorruption, status);
        Assert.NotNull(state.LoadDetail);

        // Defaults are written back so the next start is an ordinary load.
        var (second, _, _) = TestFactory.CreateState(dir);
        Assert.Equal(ConfigurationLoadStatus.Loaded, second.Initialize());
    }

    [Fact]
    public void Commit_normalises_the_draft_before_it_becomes_live()
    {
        using var dir = new TempDirectory();
        var (state, _, _) = TestFactory.CreateState(dir);
        state.Initialize();

        var draft = state.CreateDraft();
        draft.SchemaVersion = 0;
        draft.ScreenTime.WeekdayMinutes = -5;

        state.Commit(draft);

        Assert.Equal(KidShellConfiguration.CurrentSchemaVersion, state.Current.SchemaVersion);
        Assert.Equal(0, state.Current.ScreenTime.WeekdayMinutes);
    }

    [Fact]
    public void Snapshot_comparison_detects_a_change_and_ignores_a_round_trip()
    {
        var left = KidShellConfiguration.CreateDefault();
        var right = KidShellConfiguration.CreateDefault();

        Assert.True(ConfigurationSnapshot.AreEquivalent(left, right));

        right.FindApp("paint")!.IsEnabled = false;
        Assert.False(ConfigurationSnapshot.AreEquivalent(left, right));

        right.FindApp("paint")!.IsEnabled = true;
        Assert.True(ConfigurationSnapshot.AreEquivalent(left, right));
    }
}
