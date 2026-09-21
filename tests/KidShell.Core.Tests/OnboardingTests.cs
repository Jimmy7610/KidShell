using KidShell.Core.Configuration;
using KidShell.Core.Onboarding;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// First-run setup: the flow that replaced the MVP 0.1 placeholder profile.
/// </summary>
public class OnboardingTests
{
    private static (OnboardingService Onboarding, AppStateService State) Create(TempDirectory dir)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();
        return (new OnboardingService(state, logger), state);
    }

    private static OnboardingDraft ValidDraft(string name = "Lucas") => new()
    {
        Name = name,
        Age = 6,
        AvatarId = AvatarIds.Owl,
        ThemeId = ThemeIds.Space
    };

    // ---------------------------------------------------------------- 1

    [Fact]
    public void A_new_configuration_needs_onboarding()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        Assert.True(onboarding.RequiresOnboarding);
        Assert.True(state.Current.RequiresOnboarding);
        Assert.False(state.Current.Child.IsOnboardingComplete);
    }

    [Fact]
    public void A_fresh_draft_is_never_pre_filled()
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        var draft = onboarding.CreateDraft();

        Assert.Equal(string.Empty, draft.Name);
        Assert.Equal(0, draft.Age);
        Assert.Equal(string.Empty, draft.AvatarId);
        Assert.False(draft.IsComplete);
    }

    // ---------------------------------------------------------------- 2

    [Fact]
    public void A_completed_profile_survives_a_restart()
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(ValidDraft()));

        // A second service over the same directory is exactly what a restart is.
        var (_, restarted) = Create(dir);

        Assert.False(restarted.Current.RequiresOnboarding);
        Assert.True(restarted.Current.Child.IsOnboardingComplete);
        Assert.Equal("Lucas", restarted.Current.Child.Name);
    }

    // ---------------------------------------------------------------- 3

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void An_empty_name_cannot_produce_a_completed_profile(string name)
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        var draft = ValidDraft();
        draft.Name = name;

        Assert.Equal(OnboardingCompletion.Incomplete, onboarding.Complete(draft));
        Assert.True(state.Current.RequiresOnboarding);
        Assert.Equal(string.Empty, state.Current.Child.Name);
    }

    [Fact]
    public void Name_validation_rejects_empty_and_overlong_names()
    {
        Assert.Equal(NameValidation.Empty, OnboardingDraft.ValidateName(null));
        Assert.Equal(NameValidation.Empty, OnboardingDraft.ValidateName("   "));
        Assert.Equal(NameValidation.Ok, OnboardingDraft.ValidateName("Lucas"));
        Assert.Equal(
            NameValidation.TooLong,
            OnboardingDraft.ValidateName(new string('a', ChildProfile.MaxNameLength + 1)));
    }

    [Fact]
    public void Swedish_and_other_unicode_names_are_accepted()
    {
        foreach (var name in new[] { "Åsa", "Görel", "Ödman", "Linnéa", "Matteo", "Zoë", "李明" })
        {
            Assert.Equal(NameValidation.Ok, OnboardingDraft.ValidateName(name));
        }
    }

    // ---------------------------------------------------------------- 4

    [Fact]
    public void The_child_name_persists_and_is_trimmed()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        var draft = ValidDraft();
        draft.Name = "   Lucas   ";

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(draft));
        Assert.Equal("Lucas", state.Current.Child.Name);
    }

    // ---------------------------------------------------------------- 5

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(ChildProfile.OpenEndedAge)]
    public void The_selected_age_persists(int age)
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        var draft = ValidDraft();
        draft.Age = age;

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(draft));

        var (_, restarted) = Create(dir);
        Assert.Equal(age, restarted.Current.Child.Age);
    }

    // ---------------------------------------------------------------- 6

    [Fact]
    public void The_selected_avatar_persists()
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        var draft = ValidDraft();
        draft.AvatarId = AvatarIds.Robot;

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(draft));

        var (_, restarted) = Create(dir);
        Assert.Equal(AvatarIds.Robot, restarted.Current.Child.AvatarId);
    }

    [Fact]
    public void An_unchosen_avatar_blocks_completion()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        var draft = ValidDraft();
        draft.AvatarId = string.Empty;

        Assert.Equal(OnboardingCompletion.Incomplete, onboarding.Complete(draft));
        Assert.True(state.Current.RequiresOnboarding);
    }

    [Fact]
    public void At_least_twelve_avatars_are_offered()
    {
        Assert.True(AvatarIds.All.Length >= 12);
        Assert.Equal(AvatarIds.All.Length, AvatarIds.All.Distinct().Count());
    }

    // ---------------------------------------------------------------- 7

    [Theory]
    [InlineData(ThemeIds.Forest)]
    [InlineData(ThemeIds.Space)]
    [InlineData(ThemeIds.Ocean)]
    [InlineData(ThemeIds.Dino)]
    [InlineData(ThemeIds.Bright)]
    public void The_selected_theme_persists(string themeId)
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        var draft = ValidDraft();
        draft.ThemeId = themeId;

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(draft));

        var (_, restarted) = Create(dir);
        Assert.Equal(themeId, restarted.Current.Child.ThemeId);
    }

    [Fact]
    public void All_five_named_themes_exist()
    {
        Assert.Equal(5, ThemeIds.All.Length);
        Assert.All(ThemeIds.All, id => Assert.True(ThemeIds.IsKnown(id)));
    }

    [Fact]
    public void An_unknown_theme_falls_back_rather_than_breaking_the_scene()
    {
        var draft = ValidDraft();
        draft.ThemeId = "nonsense";

        Assert.Equal(ThemeIds.Default, draft.ToProfile().ThemeId);
    }

    // ---------------------------------------------------------------- 8

    [Fact]
    public void Onboarding_does_not_complete_until_finish_is_confirmed()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        // Walking through every screen without confirming the last one writes
        // nothing at all: the draft never touches the configuration file.
        var draft = onboarding.CreateDraft();

        draft.Name = "Lucas";
        Assert.True(state.Current.RequiresOnboarding);

        draft.AvatarId = AvatarIds.Fox;
        Assert.True(state.Current.RequiresOnboarding);

        draft.Age = 6;
        Assert.True(state.Current.RequiresOnboarding);

        draft.ThemeId = ThemeIds.Ocean;
        Assert.True(state.Current.RequiresOnboarding);
        Assert.Equal(string.Empty, state.Current.Child.Name);

        // Only Complete flips it.
        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(draft));
        Assert.False(state.Current.RequiresOnboarding);
    }

    [Fact]
    public void Setup_abandoned_halfway_runs_again_on_the_next_start()
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        var draft = onboarding.CreateDraft();
        draft.Name = "Lucas";
        draft.AvatarId = AvatarIds.Fox;

        // ...and the window is closed here, without Complete ever being called.
        var (restartedOnboarding, restartedState) = Create(dir);

        Assert.True(restartedOnboarding.RequiresOnboarding);
        Assert.Equal(string.Empty, restartedState.Current.Child.Name);
    }

    [Fact]
    public void A_flag_without_a_profile_still_routes_to_setup()
    {
        // Belt and braces: even a document that claims completion but has no
        // name must not produce a half-configured Child Mode.
        var config = KidShellConfiguration.CreateDefault();
        config.Child.IsOnboardingComplete = true;

        Assert.True(config.RequiresOnboarding);
    }

    // ---------------------------------------------------------------- 9

    [Fact]
    public void Restarting_setup_clears_the_profile_but_keeps_everything_else()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        onboarding.Complete(ValidDraft());

        // Give the parent some real configuration to lose.
        var draft = state.CreateDraft();
        draft.Apps.Add(new KidAppDefinition { Id = "notes", DisplayName = "Anteckna", ExecutablePath = "notepad.exe" });
        draft.FindApp("minecraft")!.IsEnabled = false;
        draft.ScreenTime.IsEnabled = true;
        draft.ScreenTime.WeekdayMinutes = 45;
        draft.Web.Mode = WebMode.Allowlist;
        draft.Web.AllowedDomains.Add("svt.se");
        draft.ParentPin.Hash = "hash";
        draft.ParentPin.Salt = "salt";
        state.Commit(draft);

        var appCountBefore = state.Current.Apps.Count;

        Assert.True(onboarding.Restart());

        // Personal details are gone...
        Assert.True(state.Current.RequiresOnboarding);
        Assert.False(state.Current.Child.IsOnboardingComplete);
        Assert.Equal(string.Empty, state.Current.Child.Name);
        Assert.Equal(0, state.Current.Child.Age);
        Assert.Equal(string.Empty, state.Current.Child.AvatarId);

        // ...and nothing else is.
        Assert.Equal(appCountBefore, state.Current.Apps.Count);
        Assert.NotNull(state.Current.FindApp("notes"));
        Assert.False(state.Current.FindApp("minecraft")!.IsEnabled);
        Assert.True(state.Current.ScreenTime.IsEnabled);
        Assert.Equal(45, state.Current.ScreenTime.WeekdayMinutes);
        Assert.Equal(WebMode.Allowlist, state.Current.Web.Mode);
        Assert.Contains("svt.se", state.Current.Web.AllowedDomains);
        Assert.True(state.Current.ParentPin.IsConfigured);
    }

    [Fact]
    public void A_restart_survives_a_restart_of_the_app()
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = Create(dir);

        onboarding.Complete(ValidDraft());
        onboarding.Restart();

        var (restarted, _) = Create(dir);

        Assert.True(restarted.RequiresOnboarding);
    }

    [Fact]
    public void Setup_can_be_completed_again_for_a_different_child()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        onboarding.Complete(ValidDraft("Lucas"));
        onboarding.Restart();

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(ValidDraft("Nora")));
        Assert.Equal("Nora", state.Current.Child.Name);
        Assert.False(state.Current.RequiresOnboarding);
    }

    [Fact]
    public void Dark_scenes_are_declared_so_on_scene_text_can_flip()
    {
        // Only the night sky needs a light text palette drawn on it.
        Assert.True(ThemeIds.IsDarkScene(ThemeIds.Space));

        foreach (var id in new[] { ThemeIds.Forest, ThemeIds.Ocean, ThemeIds.Dino, ThemeIds.Bright })
        {
            Assert.False(ThemeIds.IsDarkScene(id));
        }

        // A legacy or unknown id must resolve before it is judged.
        Assert.False(ThemeIds.IsDarkScene("meadow"));
        Assert.False(ThemeIds.IsDarkScene(null));
    }

    // ---------------------------------------------------------------- 11

    [Fact]
    public void Profile_edits_after_onboarding_still_work()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = Create(dir);

        onboarding.Complete(ValidDraft());

        // Exactly what Parent Mode does: edit a draft, commit it.
        var draft = state.CreateDraft();
        draft.Child.Name = "Nora";
        draft.Child.Age = 9;
        draft.Child.AvatarId = AvatarIds.Whale;
        draft.Child.ThemeId = ThemeIds.Dino;
        Assert.True(state.Commit(draft));

        Assert.Equal("Nora", state.Current.Child.Name);
        Assert.Equal(9, state.Current.Child.Age);
        Assert.Equal(AvatarIds.Whale, state.Current.Child.AvatarId);
        Assert.Equal(ThemeIds.Dino, state.Current.Child.ThemeId);

        // Editing the profile must not undo onboarding.
        Assert.True(state.Current.Child.IsOnboardingComplete);
        Assert.False(state.Current.RequiresOnboarding);

        var (_, restarted) = Create(dir);
        Assert.Equal("Nora", restarted.Current.Child.Name);
        Assert.False(restarted.Current.RequiresOnboarding);
    }
}
