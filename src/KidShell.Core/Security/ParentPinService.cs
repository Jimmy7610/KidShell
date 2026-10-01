using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Broker;

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
    private readonly PinAttemptThrottle _throttle;
    private readonly ProtectedPinThrottleStore? _throttleStore;
    private readonly IParentAuthenticator? _authenticator;
    private readonly ParentCapabilityHolder? _capability;

    public ParentPinService(
        IAppStateService state,
        IRuntimeEnvironment environment,
        IKidShellLogger logger,
        TimeProvider? time = null,
        ProtectedPinThrottleStore? throttleStore = null,
        IParentAuthenticator? authenticator = null,
        ParentCapabilityHolder? capability = null)
    {
        _state = state;
        _environment = environment;
        _logger = logger;
        _throttle = new PinAttemptThrottle(time);
        _throttleStore = throttleStore;
        _authenticator = authenticator;
        _capability = capability;

        // OPSV RETEST 2, ADDITIONAL FINDING. The throttle used to start empty
        // in every process, so restarting the shell returned the attempts a
        // child had already spent.
        if (_throttleStore?.Load() is { } persisted)
        {
            _throttle.Restore(persisted.FailedAttemptCount, persisted.CooldownUntilUtc);
        }
    }

    /// <summary>
    /// Writes the throttle out so a restart does not clear it.
    ///
    /// Through the same privileged path as the policy and the counter: a
    /// second storage mechanism for security state is how two of them drift
    /// apart.
    /// </summary>
    private void PersistThrottle()
    {
        if (_throttleStore is null)
        {
            return;
        }

        var (failures, until) = _throttle.Snapshot();

        _throttleStore.Save(new PinThrottleState
        {
            FailedAttemptCount = failures,
            CooldownUntilUtc = until,
            LastFailureUtc = failures > 0 ? DateTimeOffset.UtcNow : null
        });
    }

    /// <summary>How long until another attempt will be accepted.</summary>
    public TimeSpan RetryAfter => _throttle.Evaluate().RetryAfter;

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
        // Before anything else, including the shape check. A malformed entry
        // that skipped the throttle would be a free probe, and the attacker
        // controls what they type.
        var attempt = _throttle.Evaluate();

        if (!attempt.IsAllowed)
        {
            _logger.Warning("Pin",
                $"Parent PIN attempt refused; {attempt.ConsecutiveFailures} consecutive failures, " +
                $"{attempt.RetryAfter.TotalSeconds:F0}s remaining.");

            return PinVerificationResult.Throttled;
        }

        if (string.IsNullOrWhiteSpace(pin) || pin.Length != PinLength || !pin.All(char.IsAsciiDigit))
        {
            return PinVerificationResult.Malformed;
        }

        // ------------------------------------------- the privileged verifier
        //
        // PRIVILEGED BROKER HARDENING. When the security service is there it
        // does the comparison, counts the failures and owns the cooldown.
        // Everything below this point runs in the child's process, which is
        // the same process a child could replace - so where the service
        // exists, this is not the code that decides.
        if (_authenticator is { IsAvailable: true })
        {
            var answer = _authenticator.Verify(pin);

            if (answer.Result == PinVerificationResult.Correct)
            {
                // The local throttle is kept in step so the UI's countdown
                // and the service's cooldown do not disagree. It is a mirror,
                // not the authority.
                _throttle.RecordSuccess();
                _capability?.Hold(answer.Capability);

                _logger.Info("Pin", "Parent PIN accepted by the security service.");
                return PinVerificationResult.Correct;
            }

            if (answer.Result == PinVerificationResult.Incorrect)
            {
                _throttle.RecordFailure();
            }

            _logger.Info("Pin", $"The security service answered {answer.Result}.");
            return answer.Result;
        }

        if (_authenticator is not null && _environment.IsProduction)
        {
            // A production build has a verifier and cannot reach it. Falling
            // through to the comparison below would quietly move the check
            // back into the process the check exists to constrain - the
            // strictly worse option dressed as resilience. Parent Mode stays
            // shut, which is the same answer this build already gives when
            // the protected policy cannot be read.
            _logger.Error("Pin",
                "The security service is not available; Parent Mode stays closed.");

            return PinVerificationResult.Incorrect;
        }

        var settings = _state.Current.ParentPin;

        if (settings.IsConfigured)
        {
            var ok = PinHasher.Verify(pin, settings.Hash!, settings.Salt!, settings.Iterations);
            _logger.Info("Pin", ok ? "Parent PIN accepted." : "Parent PIN rejected.");
            return Record(ok);
        }

        if (_environment.IsProduction)
        {
            // No PIN and no fallback. Parent Mode stays shut rather than
            // opening on a PIN that is printed in the documentation.
            _logger.Warning("Pin", "No parent PIN configured in a production build; refusing entry.");
            return Record(false);
        }

        var devOk = PinHasher.FixedTimeEquals(pin, DevelopmentPin.Value);
        _logger.Warning("Pin", devOk
            ? "Development fallback PIN accepted. This build is not protected."
            : "Development fallback PIN rejected.");
        return Record(devOk);
    }

    /// <summary>
    /// Feeds the outcome back to the throttle.
    ///
    /// A malformed entry deliberately does not count as a failure: it never
    /// reached a comparison, so it says nothing about the PIN, and counting it
    /// would let a stray keypress start a delay.
    /// </summary>
    private PinVerificationResult Record(bool correct)
    {
        if (correct)
        {
            _throttle.RecordSuccess();
            PersistThrottle();
            return PinVerificationResult.Correct;
        }

        _throttle.RecordFailure();
        PersistThrottle();
        return PinVerificationResult.Incorrect;
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
