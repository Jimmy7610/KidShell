using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security;

/// <summary>Why Parent Mode locked itself.</summary>
public enum ParentSessionEndReason
{
    /// <summary>Still open.</summary>
    None = 0,

    /// <summary>Nothing happened for long enough that it should not stay open.</summary>
    Inactivity = 1,

    /// <summary>The parent went back to Child Mode.</summary>
    ReturnedToChild = 2,

    /// <summary>The parent chose to leave.</summary>
    Explicit = 3
}

/// <summary>
/// Whether Parent Mode is currently unlocked.
///
/// OPSV FINDING 05B — it never re-locked. A parent who opened it and walked
/// away left every setting in the product editable by whoever sat down next,
/// which on this machine is the child it is protecting them from. The PIN
/// prompt was a door with no latch.
///
/// THE SEMANTICS, STATED
/// ---------------------
/// A session begins when a correct PIN is accepted, and ends on the first of:
///
///   * fifteen minutes with no parent activity;
///   * returning to Child Mode, by any route;
///   * leaving Parent Mode explicitly.
///
/// Inactivity is measured from the last thing the parent did, not from when
/// the session started - editing settings for twenty minutes should not throw
/// somebody out mid-sentence.
///
/// Fifteen minutes is a judgement rather than a derivation. Shorter would make
/// the product tiring for a parent doing a real setup pass; longer starts to
/// mean "until the laptop sleeps", which is not a limit.
///
/// AND WHAT IT IS NOT
/// ------------------
/// Locking Parent Mode does not restrict Windows. Until a verified secure
/// configuration is active a child can still leave KidShell entirely, and
/// every surface that reports this says so. This closes the door on the
/// settings, not on the machine.
/// </summary>
public interface IParentSession
{
    /// <summary>Whether Parent Mode may be shown right now.</summary>
    bool IsUnlocked { get; }

    /// <summary>Why it locked, when it is locked.</summary>
    ParentSessionEndReason LastEndReason { get; }

    /// <summary>Raised when the session ends, so the shell can leave Parent Mode.</summary>
    event EventHandler<ParentSessionEndReason>? Ended;

    /// <summary>Starts a session. Called only after a PIN was accepted.</summary>
    void Begin();

    /// <summary>Records parent activity, restarting the inactivity window.</summary>
    void Touch();

    /// <summary>Ends the session.</summary>
    void End(ParentSessionEndReason reason);

    /// <summary>
    /// Expires the session if it is due. Called from whatever is already
    /// ticking, so this type does not need a timer of its own.
    /// </summary>
    void Evaluate();
}

/// <summary>
/// The production session. A timestamp and a rule, with an injected clock.
/// </summary>
public sealed class ParentSession : IParentSession
{
    /// <summary>How long Parent Mode may sit untouched before it re-locks.</summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often the session is asked whether it has expired.
    ///
    /// Thirty seconds, so the worst case is half a minute of Parent Mode
    /// staying open past its timeout. Finer would cost wake-ups for no benefit
    /// a person could perceive; coarser would make the fifteen minutes a
    /// suggestion rather than a limit.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly IKidShellLogger _logger;

    private DateTimeOffset _lastActivity;
    private bool _isUnlocked;

    public ParentSession(IKidShellLogger logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool IsUnlocked
    {
        get
        {
            Evaluate();
            return _isUnlocked;
        }
    }

    public ParentSessionEndReason LastEndReason { get; private set; } = ParentSessionEndReason.None;

    public event EventHandler<ParentSessionEndReason>? Ended;

    public void Begin()
    {
        _isUnlocked = true;
        _lastActivity = _time.GetUtcNow();
        LastEndReason = ParentSessionEndReason.None;

        _logger.Info("Parent", $"Parent Mode unlocked; it re-locks after {InactivityTimeout.TotalMinutes:F0} idle minutes.");
    }

    public void Touch()
    {
        if (!_isUnlocked)
        {
            return;
        }

        _lastActivity = _time.GetUtcNow();
    }

    public void End(ParentSessionEndReason reason)
    {
        if (!_isUnlocked)
        {
            return;
        }

        _isUnlocked = false;
        LastEndReason = reason;

        _logger.Info("Parent", $"Parent Mode locked ({reason}).");
        Ended?.Invoke(this, reason);
    }

    public void Evaluate()
    {
        if (!_isUnlocked)
        {
            return;
        }

        if (_time.GetUtcNow() - _lastActivity >= InactivityTimeout)
        {
            End(ParentSessionEndReason.Inactivity);
        }
    }
}
