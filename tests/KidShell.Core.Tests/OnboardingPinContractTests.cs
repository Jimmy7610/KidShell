using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Onboarding;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// A configuration store that can be told to fail, so the failure path is a
/// tested path rather than a hoped-for one.
/// </summary>
internal sealed class FailingConfigurationStore(IConfigurationStore inner) : IConfigurationStore
{
    public bool FailNextSave { get; set; }

    public int SaveAttempts { get; private set; }

    public string ConfigurationFilePath => inner.ConfigurationFilePath;

    public ConfigurationLoadResult Load() => inner.Load();

    public bool Save(KidShellConfiguration configuration)
    {
        SaveAttempts++;

        // A disk that is full, a file that is locked, a profile on a share
        // that just went away. The caller gets false, which is the contract.
        return !FailNextSave && inner.Save(configuration);
    }
}

/// <summary>
/// EXTERNAL AUDIT FINDING 01 — the parts of the Release onboarding contract
/// that were not yet pinned down.
///
/// The bulk of this finding was already fixed and already covered:
/// <see cref="ProductionPinTests"/> proves that a Release build cannot finish
/// setup without a PIN, that the published development PIN is refused as a
/// real one, that weak PINs and mismatched confirmations are rejected, that
/// what is persisted is a hash and a salt, and that no plaintext reaches the
/// configuration file.
///
/// Two things it did not cover, both about what happens when something goes
/// wrong at the last moment:
///
///  * a save that fails must not leave a half-finished setup looking finished;
///  * nothing on that path may write the PIN somewhere it can be read.
/// </summary>
public class OnboardingPinContractTests
{
    private static (OnboardingService Service, FailingConfigurationStore Store, AppStateService State, RecordingLogger Logger)
        Create(TempDirectory dir, KidShellRuntimeMode mode)
    {
        var logger = new RecordingLogger();
        var store = new FailingConfigurationStore(new JsonConfigurationStore(dir.ConfigPath, logger));
        var state = new AppStateService(store, logger);
        state.Initialize();

        var environment = mode == KidShellRuntimeMode.Production
            ? RuntimeEnvironment.Production
            : RuntimeEnvironment.Development;

        return (new OnboardingService(state, environment, logger), store, state, logger);
    }

    private static OnboardingDraft FinishedDraft(string? pin = "428071") => new()
    {
        Name = "Lucas",
        AvatarId = AvatarIds.All[0],
        Age = 6,
        ThemeId = ThemeIds.Default,
        ParentPin = pin,
        ScreenTimeEnabled = true,
        WeekdayMinutes = 60,
        WeekendMinutes = 120
    };

    // ------------------------------------------------ a save that fails

    [Fact]
    public void A_failed_save_does_not_mark_setup_as_finished()
    {
        using var dir = new TempDirectory();
        var (service, store, state, _) = Create(dir, KidShellRuntimeMode.Production);

        store.FailNextSave = true;

        var result = service.Complete(FinishedDraft());

        Assert.Equal(OnboardingCompletion.SaveFailed, result);
        Assert.True(state.Current.RequiresOnboarding,
            "setup reported a failure and then behaved as though it had succeeded");
    }

    [Fact]
    public void A_failed_save_does_not_leave_a_parent_pin_behind()
    {
        using var dir = new TempDirectory();
        var (service, store, state, _) = Create(dir, KidShellRuntimeMode.Production);

        store.FailNextSave = true;
        service.Complete(FinishedDraft());

        // Nothing was written, so nothing was configured - including the PIN.
        // A half-applied setup with a live PIN and no child profile would be
        // the worst of both: Parent Mode locked behind a code the parent may
        // not remember choosing, guarding a machine that is not set up.
        Assert.False(state.Current.ParentPin.IsConfigured);
    }

    [Fact]
    public void Setup_can_be_completed_after_a_save_failure_is_resolved()
    {
        using var dir = new TempDirectory();
        var (service, store, state, _) = Create(dir, KidShellRuntimeMode.Production);

        store.FailNextSave = true;
        Assert.Equal(OnboardingCompletion.SaveFailed, service.Complete(FinishedDraft()));

        // The disk comes back.
        store.FailNextSave = false;

        Assert.Equal(OnboardingCompletion.Completed, service.Complete(FinishedDraft()));
        Assert.False(state.Current.RequiresOnboarding);
        Assert.True(state.Current.ParentPin.IsConfigured);
    }

    // ------------------------------------------------ nothing readable is written

    [Fact]
    public void The_pin_never_appears_in_the_log()
    {
        using var dir = new TempDirectory();
        var (service, _, _, logger) = Create(dir, KidShellRuntimeMode.Production);

        const string pin = "428071";
        service.Complete(FinishedDraft(pin));

        Assert.DoesNotContain(logger.Messages, m => m.Contains(pin, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Exception?.ToString().Contains(pin, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_pin_never_appears_in_the_log_when_the_save_fails()
    {
        using var dir = new TempDirectory();
        var (service, store, _, logger) = Create(dir, KidShellRuntimeMode.Production);

        const string pin = "428071";
        store.FailNextSave = true;
        service.Complete(FinishedDraft(pin));

        // The failure path is where a plaintext secret usually escapes,
        // because somebody logs "could not save {config}" to help debugging.
        Assert.DoesNotContain(logger.Messages, m => m.Contains(pin, StringComparison.Ordinal));
    }

    [Fact]
    public void The_pin_never_appears_in_the_saved_file()
    {
        using var dir = new TempDirectory();
        var (service, _, _, _) = Create(dir, KidShellRuntimeMode.Production);

        const string pin = "428071";
        Assert.Equal(OnboardingCompletion.Completed, service.Complete(FinishedDraft(pin)));

        foreach (var file in Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(pin, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------ the contract itself

    [Fact]
    public void A_release_build_refuses_a_draft_with_no_pin()
    {
        using var dir = new TempDirectory();
        var (service, store, state, _) = Create(dir, KidShellRuntimeMode.Production);

        // Initialising the state writes a default configuration, so what
        // matters is whether COMPLETION wrote anything, not the total.
        var before = store.SaveAttempts;

        var result = service.Complete(FinishedDraft(pin: null));

        Assert.Equal(OnboardingCompletion.ParentPinRequired, result);
        Assert.True(state.Current.RequiresOnboarding);

        // Refused before anything was written, not saved and then undone.
        Assert.Equal(before, store.SaveAttempts);
    }

    [Theory]
    [InlineData("1234")]        // too short
    [InlineData("111111")]      // all one digit
    [InlineData("123456")]      // a sequence
    [InlineData(DevelopmentPin.Value)]
    public void A_release_build_refuses_a_draft_whose_pin_would_not_pass_the_policy(string pin)
    {
        using var dir = new TempDirectory();
        var (service, _, state, _) = Create(dir, KidShellRuntimeMode.Production);

        var result = service.Complete(FinishedDraft(pin));

        Assert.NotEqual(OnboardingCompletion.Completed, result);
        Assert.True(state.Current.RequiresOnboarding);
        Assert.False(state.Current.ParentPin.IsConfigured);
    }

    /// <summary>
    /// A parent who steps back past the PIN screen clears the boxes and has to
    /// retype. If that cleared draft then reached completion, a Release build
    /// would have to refuse it rather than finish without a PIN.
    /// </summary>
    [Fact]
    public void A_draft_whose_pin_was_cleared_cannot_finish_a_release_setup()
    {
        using var dir = new TempDirectory();
        var (service, _, state, _) = Create(dir, KidShellRuntimeMode.Production);

        var draft = FinishedDraft();
        Assert.True(draft.HasParentPin);

        // What stepping back does.
        draft.ParentPin = null;

        Assert.Equal(OnboardingCompletion.ParentPinRequired, service.Complete(draft));
        Assert.True(state.Current.RequiresOnboarding);
    }

    [Fact]
    public void A_pin_retyped_after_stepping_back_finishes_setup()
    {
        using var dir = new TempDirectory();
        var (service, _, state, _) = Create(dir, KidShellRuntimeMode.Production);

        var draft = FinishedDraft();
        draft.ParentPin = null;
        Assert.Equal(OnboardingCompletion.ParentPinRequired, service.Complete(draft));

        draft.ParentPin = "739284";

        Assert.Equal(OnboardingCompletion.Completed, service.Complete(draft));
        Assert.True(state.Current.ParentPin.IsConfigured);
    }

    /// <summary>
    /// The whole state machine, from an empty draft to a configured machine,
    /// asserting at each step that it is not yet finishable.
    /// </summary>
    [Fact]
    public void A_release_setup_is_unfinishable_until_every_step_is_answered()
    {
        using var dir = new TempDirectory();
        var (service, _, state, _) = Create(dir, KidShellRuntimeMode.Production);

        var draft = new OnboardingDraft();

        Assert.NotEqual(OnboardingCompletion.Completed, service.Complete(draft));

        draft.Name = "Lucas";
        Assert.NotEqual(OnboardingCompletion.Completed, service.Complete(draft));

        draft.AvatarId = AvatarIds.All[0];
        Assert.NotEqual(OnboardingCompletion.Completed, service.Complete(draft));

        draft.Age = 6;
        Assert.NotEqual(OnboardingCompletion.Completed, service.Complete(draft));

        draft.ThemeId = ThemeIds.Default;

        // Everything a child needs - and still refused, because this is a
        // Release build and no PIN has been chosen.
        Assert.Equal(OnboardingCompletion.ParentPinRequired, service.Complete(draft));

        draft.ParentPin = "428071";

        Assert.Equal(OnboardingCompletion.Completed, service.Complete(draft));
        Assert.False(state.Current.RequiresOnboarding);
    }
}
