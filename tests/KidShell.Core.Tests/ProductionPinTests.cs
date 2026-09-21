using KidShell.Core.Configuration;
using KidShell.Core.Onboarding;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The production parent-PIN rules.
///
/// The development fallback is published in the README, so a shipped build
/// that accepted it would have an effectively public Parent Mode. These tests
/// pin that it cannot, and that a production build cannot even finish setup
/// without a real PIN.
/// </summary>
public class ProductionPinTests
{
    private static ParentPinService Create(TempDirectory dir, IRuntimeEnvironment environment)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();
        return new ParentPinService(state, environment, logger);
    }

    // ------------------------------------------------ the fallback

    [Fact]
    public void A_production_build_never_accepts_the_development_pin()
    {
        using var dir = new TempDirectory();
        var service = Create(dir, TestRuntime.Production);

        Assert.False(service.IsCustomPinConfigured);

        // No PIN configured, and no way in. Parent Mode being unreachable is
        // the correct failure; opening on a documented PIN is not.
        Assert.Equal(PinVerificationResult.Incorrect, service.Verify(DevelopmentPin.Value));
        Assert.False(service.IsDevelopmentFallbackActive);
    }

    [Fact]
    public void A_development_build_accepts_it_but_says_so()
    {
        using var dir = new TempDirectory();
        var service = Create(dir, TestRuntime.Development);

        Assert.Equal(PinVerificationResult.Correct, service.Verify(DevelopmentPin.Value));

        // The UI reads this to warn that the build is not protected.
        Assert.True(service.IsDevelopmentFallbackActive);
    }

    [Fact]
    public void Setting_a_real_pin_retires_the_fallback_even_in_a_development_build()
    {
        using var dir = new TempDirectory();
        var service = Create(dir, TestRuntime.Development);

        Assert.True(service.TrySetPin("471902"));

        Assert.Equal(PinVerificationResult.Incorrect, service.Verify(DevelopmentPin.Value));
        Assert.Equal(PinVerificationResult.Correct, service.Verify("471902"));
        Assert.False(service.IsDevelopmentFallbackActive);
    }

    [Fact]
    public void The_development_pin_can_never_be_chosen_as_a_real_pin()
    {
        using var dir = new TempDirectory();

        foreach (var environment in new[] { TestRuntime.Development, TestRuntime.Production })
        {
            var service = Create(dir, environment);

            // Picking it would look configured while being public knowledge.
            Assert.False(service.TrySetPin(DevelopmentPin.Value));
        }

        Assert.Equal(PinValidation.ReservedDevelopmentPin, ParentPinPolicy.Validate(DevelopmentPin.Value));
    }

    // ------------------------------------------------ policy

    [Theory]
    [InlineData(null, PinValidation.Empty)]
    [InlineData("", PinValidation.Empty)]
    [InlineData("   ", PinValidation.Empty)]
    [InlineData("12ab56", PinValidation.NotNumeric)]
    [InlineData("1234", PinValidation.WrongLength)]
    [InlineData("12345678", PinValidation.WrongLength)]
    [InlineData("000000", PinValidation.Repeated)]
    [InlineData("777777", PinValidation.Repeated)]
    [InlineData("123456", PinValidation.Sequential)]
    [InlineData("654321", PinValidation.Sequential)]
    [InlineData("246810", PinValidation.ReservedDevelopmentPin)]
    [InlineData("471902", PinValidation.Ok)]
    [InlineData("908172", PinValidation.Ok)]
    public void Pin_policy_rejects_the_obvious_choices(string? pin, PinValidation expected) =>
        Assert.Equal(expected, ParentPinPolicy.Validate(pin));

    [Fact]
    public void A_confirmation_must_match()
    {
        Assert.Equal(PinValidation.Ok, ParentPinPolicy.ValidatePair("471902", "471902"));
        Assert.Equal(PinValidation.ConfirmationMismatch, ParentPinPolicy.ValidatePair("471902", "471903"));

        // A bad PIN is reported as bad before the mismatch is considered, so
        // the parent fixes the real problem first.
        Assert.Equal(PinValidation.Sequential, ParentPinPolicy.ValidatePair("123456", "999999"));
    }

    [Fact]
    public void A_rejected_pin_is_never_persisted()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();
        var service = new ParentPinService(state, TestRuntime.Production, logger);

        Assert.False(service.TrySetPin("000000"));
        Assert.False(state.Current.ParentPin.IsConfigured);
    }

    [Fact]
    public void The_pin_is_never_written_in_plain_text()
    {
        using var dir = new TempDirectory();
        var service = Create(dir, TestRuntime.Production);

        Assert.True(service.TrySetPin("471902"));

        var onDisk = File.ReadAllText(dir.ConfigPath);

        Assert.DoesNotContain("471902", onDisk, StringComparison.Ordinal);
        Assert.Contains("hash", onDisk, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ onboarding

    private static (OnboardingService Onboarding, AppStateService State) CreateOnboarding(
        TempDirectory dir,
        IRuntimeEnvironment environment)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();
        return (new OnboardingService(state, environment, logger), state);
    }

    private static OnboardingDraft ProfileOnlyDraft() => new()
    {
        Name = "Lucas",
        Age = 6,
        AvatarId = AvatarIds.Owl,
        ThemeId = ThemeIds.Forest
    };

    [Fact]
    public void A_production_build_cannot_finish_setup_without_a_parent_pin()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = CreateOnboarding(dir, TestRuntime.Production);

        var result = onboarding.Complete(ProfileOnlyDraft());

        Assert.Equal(OnboardingCompletion.ParentPinRequired, result);

        // Nothing was written: setup is not half-done, it simply did not run.
        Assert.True(state.Current.RequiresOnboarding);
        Assert.Equal(string.Empty, state.Current.Child.Name);
    }

    [Fact]
    public void A_production_build_finishes_once_a_pin_is_chosen()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = CreateOnboarding(dir, TestRuntime.Production);

        var draft = ProfileOnlyDraft();
        draft.ParentPin = "471902";

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(draft));

        Assert.False(state.Current.RequiresOnboarding);
        Assert.True(state.Current.ParentPin.IsConfigured);
    }

    [Fact]
    public void A_pin_chosen_during_setup_is_hashed_not_stored()
    {
        using var dir = new TempDirectory();
        var (onboarding, _) = CreateOnboarding(dir, TestRuntime.Production);

        var draft = ProfileOnlyDraft();
        draft.ParentPin = "471902";
        onboarding.Complete(draft);

        Assert.DoesNotContain("471902", File.ReadAllText(dir.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pin_chosen_during_setup_actually_unlocks_parent_mode()
    {
        using var dir = new TempDirectory();
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();

        var onboarding = new OnboardingService(state, TestRuntime.Production, logger);
        var draft = ProfileOnlyDraft();
        draft.ParentPin = "471902";
        onboarding.Complete(draft);

        var pins = new ParentPinService(state, TestRuntime.Production, logger);

        Assert.Equal(PinVerificationResult.Correct, pins.Verify("471902"));
        Assert.Equal(PinVerificationResult.Incorrect, pins.Verify(DevelopmentPin.Value));
    }

    [Fact]
    public void A_development_build_may_finish_without_one()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = CreateOnboarding(dir, TestRuntime.Development);

        Assert.Equal(OnboardingCompletion.Completed, onboarding.Complete(ProfileOnlyDraft()));
        Assert.False(state.Current.RequiresOnboarding);
    }

    [Fact]
    public void A_draft_refuses_a_policy_violating_pin()
    {
        var draft = ProfileOnlyDraft();

        draft.ParentPin = "123456";
        Assert.False(draft.HasParentPin);
        Assert.False(draft.IsCompleteFor(KidShellRuntimeMode.Production));

        draft.ParentPin = "471902";
        Assert.True(draft.HasParentPin);
        Assert.True(draft.IsCompleteFor(KidShellRuntimeMode.Production));
    }

    [Fact]
    public void Re_running_setup_keeps_the_parent_pin()
    {
        using var dir = new TempDirectory();
        var (onboarding, state) = CreateOnboarding(dir, TestRuntime.Production);

        var draft = ProfileOnlyDraft();
        draft.ParentPin = "471902";
        onboarding.Complete(draft);

        Assert.True(onboarding.Restart());

        // Handing the machine to another child must not unlock Parent Mode.
        Assert.True(state.Current.ParentPin.IsConfigured);
        Assert.True(state.Current.RequiresOnboarding);
    }

    // ------------------------------------------------ developer mode

    [Fact]
    public void Developer_mode_is_a_function_of_the_build_only()
    {
        Assert.True(new DeveloperOptions(RuntimeEnvironment.Development).DeveloperMode);
        Assert.False(new DeveloperOptions(RuntimeEnvironment.Production).DeveloperMode);

        // The safe default: anything that cannot tell is production.
        Assert.False(DeveloperOptions.Production.DeveloperMode);
    }

    [Fact]
    public void A_development_build_always_shows_the_watermark()
    {
        // A build with relaxed security must never look like a shipped one.
        Assert.True(new DeveloperOptions(RuntimeEnvironment.Development).ShowDevelopmentWatermark);
        Assert.False(new DeveloperOptions(RuntimeEnvironment.Production).ShowDevelopmentWatermark);
    }

    [Fact]
    public void Developer_options_expose_no_way_to_force_the_mode_on()
    {
        // Every public constructor must take the environment - none may take a
        // bare bool, which would be exactly the backdoor this design removes.
        var constructors = typeof(DeveloperOptions).GetConstructors();

        Assert.All(constructors, c =>
        {
            var parameters = c.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(IRuntimeEnvironment), parameters[0].ParameterType);
        });
    }
}
