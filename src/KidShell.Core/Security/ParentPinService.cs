using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security;

/// <summary>
/// Default <see cref="IParentPinService"/>: PBKDF2 hash held inside the
/// KidShell configuration document, with a clearly-marked development
/// fallback while no PIN has been chosen.
/// </summary>
public sealed class ParentPinService : IParentPinService
{
    private readonly IAppStateService _state;
    private readonly IDeveloperOptions _developerOptions;
    private readonly IKidShellLogger _logger;

    public ParentPinService(IAppStateService state, IDeveloperOptions developerOptions, IKidShellLogger logger)
    {
        _state = state;
        _developerOptions = developerOptions;
        _logger = logger;
    }

    public int PinLength => 6;

    public bool IsCustomPinConfigured => _state.Current.ParentPin.IsConfigured;

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

        if (!_developerOptions.DeveloperMode)
        {
            // No PIN configured and no development fallback: stay locked.
            _logger.Warning("Pin", "No parent PIN configured and developer mode is off; refusing entry.");
            return PinVerificationResult.Incorrect;
        }

        var devOk = PinHasher.FixedTimeEquals(pin, DevelopmentPin.Value);
        _logger.Info("Pin", devOk
            ? "Development fallback PIN accepted."
            : "Development fallback PIN rejected.");
        return devOk ? PinVerificationResult.Correct : PinVerificationResult.Incorrect;
    }

    public bool TrySetPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != PinLength || !pin.All(char.IsAsciiDigit))
        {
            return false;
        }

        var (hash, salt) = PinHasher.Hash(pin);
        var settings = _state.Current.ParentPin;
        settings.Hash = hash;
        settings.Salt = salt;
        settings.Iterations = PinHasher.DefaultIterations;

        if (!_state.SaveCurrent())
        {
            _logger.Error("Pin", "New parent PIN could not be persisted.");
            return false;
        }

        _logger.Info("Pin", "Parent PIN updated.");
        return true;
    }
}
