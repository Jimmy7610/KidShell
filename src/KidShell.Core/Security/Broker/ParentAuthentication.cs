using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Broker;

/// <summary>What the privileged side said about a PIN.</summary>
/// <param name="Result">The answer the UI shows.</param>
/// <param name="Capability">
/// Issued only on success, and only by the service. Null otherwise.
/// </param>
/// <param name="RetryAfter">How long to wait, when the answer is Throttled.</param>
public sealed record ParentAuthentication(
    PinVerificationResult Result, string? Capability, TimeSpan RetryAfter)
{
    public static ParentAuthentication Incorrect(TimeSpan retryAfter) =>
        new(PinVerificationResult.Incorrect, null, retryAfter);

    public static ParentAuthentication Throttled(TimeSpan retryAfter) =>
        new(PinVerificationResult.Throttled, null, retryAfter);
}

/// <summary>
/// Checks a parent's PIN somewhere the child's process cannot decide the
/// answer.
///
/// WHY THE COMPARISON MOVED
/// ------------------------
/// A throttle enforced by the process being throttled is a suggestion, and a
/// hash comparison performed by a program running as the child is one a
/// modified copy of that program can simply return true from. Both of those
/// were true before this pass, and no amount of care in
/// <see cref="ParentPinService"/> could have fixed either, because the
/// problem was WHERE the code ran rather than what it did.
///
/// So the service holds the policy, does the PBKDF2 comparison, counts the
/// failures and owns the cooldown. The child's process asks and is told.
/// </summary>
public interface IParentAuthenticator
{
    /// <summary>Whether the privileged verifier can be reached.</summary>
    bool IsAvailable { get; }

    ParentAuthentication Verify(string pin);
}

/// <summary>
/// Asks the security service.
///
/// WHAT THIS STILL DOES NOT SOLVE
/// ------------------------------
/// A compromised KidShell.App can read the PIN as the parent types it,
/// because the parent types it into that process. Nothing on this side of the
/// boundary can prevent that, and moving the comparison does not pretend to.
///
/// What it does change is everything after: the attempt is counted where the
/// attacker cannot reach the counter, the cooldown is applied where the
/// attacker cannot shorten it, and a capability lasts twenty-five minutes
/// rather than forever. Guessing becomes expensive instead of free, which is
/// what a throttle is for. The remaining exposure needs code signing and a
/// trusted input path, and is recorded as such.
/// </summary>
public sealed class BrokeredParentAuthenticator : IParentAuthenticator
{
    private readonly IElevatedBrokerClient _broker;
    private readonly IKidShellLogger _logger;

    public BrokeredParentAuthenticator(IElevatedBrokerClient broker, IKidShellLogger logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public bool IsAvailable => _broker.IsAvailable;

    public ParentAuthentication Verify(string pin)
    {
        var response = _broker.Send(new ElevatedRequest
        {
            Kind = ElevatedOperationKind.VerifyParentPin,
            RequestId = Guid.NewGuid().ToString("n"),
            ParentPinAttempt = pin,
            DryRun = false
        });

        var retryAfter = TimeSpan.FromSeconds(Math.Max(0, response.RetryAfterSeconds));

        if (response.Success && !string.IsNullOrEmpty(response.ParentCapability))
        {
            return new ParentAuthentication(
                PinVerificationResult.Correct, response.ParentCapability, TimeSpan.Zero);
        }

        return response.Reason switch
        {
            BrokerFailureReason.PinThrottled => ParentAuthentication.Throttled(retryAfter),
            BrokerFailureReason.PinIncorrect => ParentAuthentication.Incorrect(retryAfter),

            // Anything else - the service is gone, the protocol disagreed,
            // the policy could not be read - is not an answer about the PIN.
            // Treated as a refusal, because the alternative is opening Parent
            // Mode on the strength of an error.
            _ => Unreachable(response)
        };
    }

    private ParentAuthentication Unreachable(ElevatedResponse response)
    {
        _logger.Error(BrokerAudit.Category,
            $"The parent PIN could not be verified ({response.Reason}): {response.Message}");

        return ParentAuthentication.Incorrect(TimeSpan.Zero);
    }
}

/// <summary>
/// Holds the capability the service issued, for as long as the parent session
/// lasts.
///
/// In memory and nowhere else. A capability written to disk would be a
/// capability a child could read, and the point of it is that they cannot.
/// </summary>
public sealed class ParentCapabilityHolder
{
    private string? _value;

    /// <summary>The current capability, or null when no parent is authenticated.</summary>
    public string? Value
    {
        get => _value;
        private set => _value = value;
    }

    public bool IsHeld => !string.IsNullOrEmpty(_value);

    public void Hold(string? capability) => Value = capability;

    /// <summary>Forgets it. Called when the parent session ends, for any reason.</summary>
    public void Release() => Value = null;
}
