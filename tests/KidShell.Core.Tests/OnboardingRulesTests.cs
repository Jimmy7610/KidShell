using KidShell.Core.Configuration;
using KidShell.Core.Onboarding;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// What first-run setup produces, and what it refuses to produce.
///
/// The bug these exist for: a Release build could not finish setup at all.
/// The draft required a parent PIN in production, the service refused without
/// one, and no screen ever collected it - so the shipping configuration was a
/// dead end that a developer build never hit.
/// </summary>
public class OnboardingRulesTests
{
    private static OnboardingDraft FullDraft(string? pin = null) => new()
    {
        Name = "Nils",
        Age = 7,
        AvatarId = AvatarIds.All[0],
        ThemeId = ThemeIds.Default,
        ParentPin = pin
    };

    private static (OnboardingService Service, StubState State) Build(KidShellRuntimeMode mode)
    {
        var state = new StubState();

        var environment = mode == KidShellRuntimeMode.Development
            ? RuntimeEnvironment.Development
            : RuntimeEnvironment.Production;

        return (new OnboardingService(state, environment, new RecordingLogger()), state);
    }

    // ------------------------------------------------------------ the PIN

    [Fact]
    public void A_production_build_refuses_to_finish_without_a_parent_PIN()
    {
        var (service, _) = Build(KidShellRuntimeMode.Production);

        var result = service.Complete(FullDraft(pin: null));

        // Named specifically, not as a generic failure. The setup screen sends
        // the parent back to the PIN step on this result, which it could not do
        // if the answer were just "something went wrong".
        Assert.Equal(OnboardingCompletion.ParentPinRequired, result);
    }

    [Fact]
    public void A_production_build_finishes_with_a_real_PIN()
    {
        var (service, state) = Build(KidShellRuntimeMode.Production);

        Assert.Equal(OnboardingCompletion.Completed, service.Complete(FullDraft("417392")));

        // IsConfigured is the real question: a hash with no salt is not a
        // usable PIN, and asserting on one field alone would miss that.
        Assert.True(state.Committed!.ParentPin.IsConfigured);
    }

    [Fact]
    public void A_development_build_may_finish_without_one()
    {
        // The published fallback covers Debug, and a developer stopping to
        // invent a PIN on every reset is friction with no security value.
        var (service, _) = Build(KidShellRuntimeMode.Development);

        Assert.Equal(OnboardingCompletion.Completed, service.Complete(FullDraft(pin: null)));
    }

    [Fact]
    public void The_PIN_is_hashed_and_never_stored_as_typed()
    {
        var (service, state) = Build(KidShellRuntimeMode.Production);

        service.Complete(FullDraft("417392"));

        var pin = state.Committed!.ParentPin;

        Assert.True(pin.IsConfigured);
        Assert.DoesNotContain("417392", pin.Hash ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("417392", pin.Salt ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("000000")]
    [InlineData("246810")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public void A_PIN_that_fails_policy_is_not_written(string pin)
    {
        var (service, state) = Build(KidShellRuntimeMode.Production);

        var result = service.Complete(FullDraft(pin));

        // Either refused outright, or completed without adopting the bad PIN.
        // What must never happen is 123456 becoming the parent's PIN.
        if (result == OnboardingCompletion.Completed)
        {
            Assert.False(state.Committed!.ParentPin.IsConfigured);
        }
        else
        {
            Assert.Equal(OnboardingCompletion.ParentPinRequired, result);
        }
    }

    [Fact]
    public void The_development_fallback_PIN_is_refused_as_a_chosen_PIN()
    {
        // Choosing the published fallback deliberately would leave a machine
        // whose PIN is in the documentation.
        Assert.Equal(PinValidation.ReservedDevelopmentPin,
            ParentPinPolicy.Validate(DevelopmentPin.Value));
    }

    // ----------------------------------------------------------- the rules

    [Fact]
    public void The_rules_chosen_during_setup_are_applied()
    {
        var (service, state) = Build(KidShellRuntimeMode.Development);

        var draft = FullDraft();
        draft.ScreenTimeEnabled = true;
        draft.WeekdayMinutes = 45;
        draft.WeekendMinutes = 90;
        draft.WebMode = WebMode.Allowlist;

        Assert.Equal(OnboardingCompletion.Completed, service.Complete(draft));

        var config = state.Committed!;

        // Applied, not left for the parent to discover. Finishing setup with a
        // machine that has no limits and no prompt to add any is how a
        // parental-control product ends up unused.
        Assert.True(config.ScreenTime.IsEnabled);
        Assert.Equal(45, config.ScreenTime.WeekdayMinutes);
        Assert.Equal(90, config.ScreenTime.WeekendMinutes);
        Assert.Equal(WebMode.Allowlist, config.Web.Mode);
    }

    [Fact]
    public void A_parent_who_switches_screen_time_off_gets_it_off()
    {
        var (service, state) = Build(KidShellRuntimeMode.Development);

        var draft = FullDraft();
        draft.ScreenTimeEnabled = false;

        service.Complete(draft);

        Assert.False(state.Committed!.ScreenTime.IsEnabled);
    }

    [Fact]
    public void The_defaults_are_workable_rather_than_empty()
    {
        // A parent who accepts every suggestion should get something sensible,
        // not a machine with no rules.
        var draft = new OnboardingDraft();

        Assert.True(draft.ScreenTimeEnabled);
        Assert.True(draft.WeekdayMinutes > 0);
        Assert.True(draft.WeekendMinutes >= draft.WeekdayMinutes);
        Assert.Equal(WebMode.NoBrowser, draft.WebMode);
    }

    // -------------------------------------------------------- completeness

    [Fact]
    public void An_unfinished_draft_is_refused_before_the_PIN_is_considered()
    {
        var (service, _) = Build(KidShellRuntimeMode.Production);

        var draft = FullDraft("417392");
        draft.Name = string.Empty;

        Assert.Equal(OnboardingCompletion.Incomplete, service.Complete(draft));
    }

    [Fact]
    public void Nothing_is_written_when_completion_is_refused()
    {
        var (service, state) = Build(KidShellRuntimeMode.Production);

        service.Complete(FullDraft(pin: null));

        // Closing setup half-way must leave nothing behind, so it simply runs
        // again next time.
        Assert.Null(state.Committed);
    }

    private sealed class StubState : IAppStateService
    {
        public KidShellConfiguration Current { get; private set; } = KidShellConfiguration.CreateDefault();

        public KidShellConfiguration? Committed { get; private set; }

        public ConfigurationLoadStatus LoadStatus => ConfigurationLoadStatus.Loaded;

        public string? LoadDetail => null;

        public string ConfigurationFilePath => "(in-memory)";

        public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

        public ConfigurationLoadStatus Initialize() => ConfigurationLoadStatus.Loaded;

        public KidShellConfiguration CreateDraft() => Current.Clone();

        public bool Commit(KidShellConfiguration draft)
        {
            Committed = draft;
            Current = draft;
            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(Current));
            return true;
        }

        public bool SaveCurrent() => true;
    }
}
