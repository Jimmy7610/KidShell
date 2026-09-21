namespace KidShell.Core.Security;

public enum PinVerificationResult
{
    /// <summary>The PIN matched. Parent Mode may open.</summary>
    Correct = 0,

    /// <summary>The PIN did not match. Nothing about the expected PIN is revealed.</summary>
    Incorrect = 1,

    /// <summary>The entry was empty or the wrong length.</summary>
    Malformed = 2
}

/// <summary>
/// Gate in front of Parent Mode. The abstraction exists so that MVP 0.1's
/// PBKDF2-in-the-config-file implementation can later be swapped for a
/// Windows Credential Manager or Hello backed one without touching the UI.
/// </summary>
public interface IParentPinService
{
    /// <summary>Expected number of digits in the PIN.</summary>
    int PinLength { get; }

    /// <summary>True when a real PIN has been set and the development fallback is not in play.</summary>
    bool IsCustomPinConfigured { get; }

    /// <summary>
    /// True when the published development fallback PIN would currently be
    /// accepted. Surfaced in the UI rather than hidden: a build in this state
    /// has an effectively public Parent Mode.
    /// </summary>
    bool IsDevelopmentFallbackActive { get; }

    PinVerificationResult Verify(string pin);

    /// <summary>Replaces the stored PIN. Returns false if the new PIN is not valid.</summary>
    bool TrySetPin(string pin);
}
