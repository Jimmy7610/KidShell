using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

public class ParentPinTests
{
    private static ParentPinService Create(TempDirectory dir, bool developerMode = true)
    {
        var (state, _, logger) = TestFactory.CreateState(dir);
        state.Initialize();
        return new ParentPinService(state, new StubDeveloperOptions(developerMode), logger);
    }

    [Fact]
    public void The_development_pin_unlocks_parent_mode_while_no_pin_is_set()
    {
        using var dir = new TempDirectory();
        var service = Create(dir);

        Assert.False(service.IsCustomPinConfigured);
        Assert.Equal(PinVerificationResult.Correct, service.Verify(DevelopmentPin.Value));
    }

    [Fact]
    public void A_wrong_pin_is_rejected()
    {
        using var dir = new TempDirectory();
        var service = Create(dir);

        Assert.Equal(PinVerificationResult.Incorrect, service.Verify("111111"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("12345678")]
    [InlineData("12a456")]
    public void Malformed_entries_are_rejected_without_revealing_anything(string entry)
    {
        using var dir = new TempDirectory();
        var service = Create(dir);

        Assert.Equal(PinVerificationResult.Malformed, service.Verify(entry));
    }

    [Fact]
    public void Without_developer_mode_an_unconfigured_pin_stays_locked()
    {
        using var dir = new TempDirectory();
        var service = Create(dir, developerMode: false);

        // The development fallback must never let anyone in once KidShell
        // ships with developer mode off.
        Assert.Equal(PinVerificationResult.Incorrect, service.Verify(DevelopmentPin.Value));
    }

    [Fact]
    public void Setting_a_pin_replaces_the_development_fallback()
    {
        using var dir = new TempDirectory();
        var service = Create(dir);

        Assert.True(service.TrySetPin("135791"));

        Assert.True(service.IsCustomPinConfigured);
        Assert.Equal(PinVerificationResult.Correct, service.Verify("135791"));
        Assert.Equal(PinVerificationResult.Incorrect, service.Verify(DevelopmentPin.Value));
    }

    [Fact]
    public void A_new_pin_is_persisted_and_still_works_after_a_restart()
    {
        using var dir = new TempDirectory();

        Create(dir).TrySetPin("135791");

        var afterRestart = Create(dir);

        Assert.True(afterRestart.IsCustomPinConfigured);
        Assert.Equal(PinVerificationResult.Correct, afterRestart.Verify("135791"));
    }

    [Fact]
    public void The_pin_is_never_written_to_disk_in_readable_form()
    {
        using var dir = new TempDirectory();
        Create(dir).TrySetPin("135791");

        var contents = File.ReadAllText(dir.ConfigPath);

        Assert.DoesNotContain("135791", contents);
        Assert.DoesNotContain(DevelopmentPin.Value, contents);
        Assert.Contains("\"hash\"", contents);
        Assert.Contains("\"salt\"", contents);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("")]
    public void An_invalid_new_pin_is_refused(string pin)
    {
        using var dir = new TempDirectory();
        var service = Create(dir);

        Assert.False(service.TrySetPin(pin));
        Assert.False(service.IsCustomPinConfigured);
    }
}

public class PinHasherTests
{
    [Fact]
    public void The_same_pin_hashed_twice_produces_different_salts()
    {
        var (hashA, saltA) = PinHasher.Hash("246810");
        var (hashB, saltB) = PinHasher.Hash("246810");

        Assert.NotEqual(saltA, saltB);
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void Verify_accepts_the_original_and_rejects_anything_else()
    {
        var (hash, salt) = PinHasher.Hash("246810");

        Assert.True(PinHasher.Verify("246810", hash, salt, PinHasher.DefaultIterations));
        Assert.False(PinHasher.Verify("246811", hash, salt, PinHasher.DefaultIterations));
    }

    [Fact]
    public void Verify_fails_safe_on_damaged_material()
    {
        var (hash, salt) = PinHasher.Hash("246810");

        Assert.False(PinHasher.Verify("246810", "not base64!", salt, PinHasher.DefaultIterations));
        Assert.False(PinHasher.Verify("246810", hash, salt, 0));
        Assert.False(PinHasher.Verify("", hash, salt, PinHasher.DefaultIterations));
    }
}
