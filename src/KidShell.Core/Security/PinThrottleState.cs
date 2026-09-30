using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Security;

/// <summary>
/// The failed-attempt state, as it survives a restart.
///
/// OPSV RETEST 2, ADDITIONAL FINDING. The throttle lived in memory, so a new
/// process started with a clean slate. A child who can restart the shell - by
/// closing it, or by making it crash - got their attempts back, and the
/// progressive delay priced nothing.
///
/// WHAT IS NOT IN HERE
/// -------------------
/// No PIN, no attempted values, no hash candidates. It records that failures
/// happened and until when they cost something. Writing what was typed would
/// turn a throttle into a log of a child's guesses at their parent's code.
/// </summary>
public sealed class PinThrottleState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Consecutive wrong answers. Reset by a correct one.</summary>
    public int FailedAttemptCount { get; set; }

    /// <summary>
    /// When another attempt will be accepted, in UTC.
    ///
    /// UTC because the whole point is to survive a restart, and a restart can
    /// cross a daylight-saving boundary or a time-zone change.
    /// </summary>
    public DateTimeOffset? CooldownUntilUtc { get; set; }

    /// <summary>
    /// The last failure, in UTC.
    ///
    /// Kept so a cooldown that is impossibly far in the future can be
    /// recognised as tampering rather than obeyed.
    /// </summary>
    public DateTimeOffset? LastFailureUtc { get; set; }

    [JsonIgnore]
    public bool IsEmpty => FailedAttemptCount == 0 && CooldownUntilUtc is null;
}

/// <summary>
/// Reads and writes the throttle through the protected store.
///
/// Uses the same privileged write path as the policy and the counter, because
/// inventing a second storage mechanism for security state is how the two
/// drift apart in exactly the way this pass keeps finding.
/// </summary>
public sealed class ProtectedPinThrottleStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IProtectedStateReader _reader;
    private readonly IProtectedStateWriter _writer;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;

    public ProtectedPinThrottleStore(
        IProtectedStateReader reader,
        IProtectedStateWriter writer,
        IKidShellLogger logger,
        TimeProvider? time = null)
    {
        _reader = reader;
        _writer = writer;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads the throttle, conservatively.
    ///
    /// Every failure mode here resolves towards "there is a cooldown" rather
    /// than "there is not", because the alternative hands out free attempts.
    /// The one thing it will not do is lock a parent out permanently, which is
    /// why an absurd cooldown is clamped rather than obeyed.
    /// </summary>
    public PinThrottleState Load()
    {
        var document = _reader.Read(ProtectedDocument.PinThrottleState);

        if (string.IsNullOrWhiteSpace(document))
        {
            // Nothing recorded. Distinguishing "never any failures" from
            // "somebody deleted it" is not possible here and does not need to
            // be: the protected store is one the child cannot empty, and in a
            // development build the throttle is not a security control anyway.
            return new PinThrottleState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<PinThrottleState>(document, Options);

            if (state is null || state.SchemaVersion > PinThrottleState.CurrentSchemaVersion)
            {
                return Conservative("the throttle state is not in a form this build understands");
            }

            state.FailedAttemptCount = Math.Max(0, state.FailedAttemptCount);

            var clamped = Clamp(state.CooldownUntilUtc);

            if (clamped != state.CooldownUntilUtc)
            {
                // Written back straight away, not just clamped in memory.
                //
                // Clamping on every load without persisting would re-anchor an
                // absurd stored value to "two minutes from now" on every
                // restart, which is a permanent throttle wearing a bound. The
                // clamped value has to become the durable one so that it
                // actually expires.
                state.CooldownUntilUtc = clamped;

                _logger.Warning("Pin",
                    "The stored PIN cooldown was out of range and has been bounded.");

                Save(state);
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger.Warning("Pin", "The throttle state could not be read.", ex);
            return Conservative("the throttle state could not be parsed");
        }
    }

    /// <summary>
    /// What to assume when the record cannot be trusted.
    ///
    /// A cooldown, not a clean slate. Corrupting the file must not be a way to
    /// buy attempts - it is the same shape as deleting the screen-time
    /// counter, and the same answer.
    /// </summary>
    private PinThrottleState Conservative(string reason)
    {
        _logger.Warning("Pin", $"Applying a cooldown because {reason}.");

        return new PinThrottleState
        {
            FailedAttemptCount = PinAttemptPolicy.FreeAttempts + 1,
            CooldownUntilUtc = _time.GetUtcNow() + PinAttemptPolicy.FirstDelay,
            LastFailureUtc = _time.GetUtcNow()
        };
    }

    /// <summary>
    /// Bounds a persisted cooldown.
    ///
    /// The value comes off disk, so it comes from whoever can write that file.
    /// A cooldown in the year 3000 would lock a parent out of their own
    /// computer forever, which is a worse failure than a few free attempts -
    /// so anything beyond the policy maximum is treated as the maximum.
    ///
    /// A clock that has been wound BACK is handled by this too: the stored
    /// instant stays in the future for longer, which is the conservative
    /// direction, and it can never exceed the cap.
    /// </summary>
    private DateTimeOffset? Clamp(DateTimeOffset? until)
    {
        if (until is not { } value)
        {
            return null;
        }

        var ceiling = _time.GetUtcNow() + PinAttemptPolicy.MaximumDelay;

        return value > ceiling ? ceiling : value;
    }

    /// <summary>Writes the throttle. A failure is logged and not fatal.</summary>
    public void Save(PinThrottleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var result = _writer.SavePinThrottleState(JsonSerializer.Serialize(state, Options));

        if (!result.Success)
        {
            // Not fatal: the in-memory throttle still applies for this
            // process. What is lost is the part that survives a restart, and
            // saying so is more useful than pretending otherwise.
            _logger.Warning("Pin",
                $"The PIN throttle could not be persisted ({result.Status}); " +
                "a restart would clear it.");
        }
    }
}
