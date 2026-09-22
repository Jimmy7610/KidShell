using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;

namespace KidShell.Core.Security;

/// <summary>
/// Default <see cref="IParentPinService"/>: a PBKDF2 hash held inside the
/// KidShell configuration document.
///
/// The development fallback is gated on the build, not on configuration. In a
/// production build <see cref="DevelopmentPin"/> is refused even when no PIN
/// has been set — a state that then leaves Parent Mode unreachable, which is
/// the correct failure. Onboarding is what guarantees a production install
/// always has a real PIN before it finishes.
/// </summary>
public sealed class ParentPinService : IParentPinService
{
    private readonly IAppStateService _state;
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;

    public ParentPinService(IAppStateService state, IRuntimeEnvironment environment, IKidShellLogger logger)
    {
        _state = state;
        _environment = environment;
        _logger = logger;
    }

    public int PinLength => ParentPinPolicy.RequiredLength;

    public bool IsCustomPinConfigured => _state.Current.ParentPin.IsConfigured;

    /// <summary>
    /// Whether the published fallback PIN would currently be accepted. Shown
    /// prominently in the UI, because a build in this state is not protected.
    /// </summary>
    public bool IsDevelopmentFallbackActive =>
        _environment.IsDevelopment && !IsCustomPinConfigured;

    public PinVerificationResult Verify(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != PinLength || !pin.All(char.IsAsciiDigit))
        {
            return PinVerificationResult.Malformed;
        }

        var settings = _state.Current.ParentPin;

        if (settings.IsConfigured)
        {
            var ok = PinHasher.Verify(pin, settings.Hash!, settings.Salt!, settings.Iterations);
            _logger.Info("Pin", ok ? "Parent PIN accepted." : "Parent PIN rejected.");
            return ok ? PinVerificationResult.Correct : PinVerificationResult.Incorrect;
        }

        if (_environment.IsProduction)
        {
            // No PIN and no fallback. Parent Mode stays shut rather than
            // opening on a PIN that is printed in the documentation.
            _logger.Warning("Pin", "No parent PIN configured in a production build; refusing entry.");
            return PinVerificationResult.Incorrect;
        }

        var devOk = PinHasher.FixedTimeEquals(pin, DevelopmentPin.Value);
        _logger.Warning("Pin", devOk
            ? "Development fallback PIN accepted. This build is not protected."
            : "Development fallback PIN rejected.");
        return devOk ? PinVerificationResult.Correct : PinVerificationResult.Incorrect;
    }

    public bool TrySetPin(string pin)
    {
        if (ParentPinPolicy.Validate(pin) != PinValidation.Ok)
        {
            _logger.Warning("Pin", "A proposed parent PIN was refused by policy.");
            return false;
        }

        var (hash, salt) = PinHasher.Hash(pin);
        var draft = _state.CreateDraft();
        draft.ParentPin.Hash = hash;
        draft.ParentPin.Salt = salt;
        draft.ParentPin.Iterations = PinHasher.DefaultIterations;

        if (!_state.Commit(draft))
        {
            _logger.Error("Pin", "New parent PIN could not be persisted.");
            return false;
        }

        _logger.Info("Pin", "Parent PIN updated.");
        return true;
    }
}
