using System.Security.Cryptography;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// The capabilities the service has issued, and to whom.
///
/// WHAT MAKES THIS A CAPABILITY AND NOT A CLAIM
/// --------------------------------------------
/// A claim is something a caller asserts and the service believes. The
/// request field <c>isParent</c> would have been a claim, and so would a
/// token the service inspected rather than recognised.
///
/// What is here instead: the SERVICE generates 256 bits from the system CSPRNG
/// after IT has verified a PIN, remembers them against the SID and Windows
/// session of the caller it verified, and later answers "did I issue this, to
/// you, and is it still valid". Nothing is encoded in the value, so there is
/// nothing in it for a caller to forge or to alter. A caller that has one
/// either received it or guessed 256 bits.
///
/// Bound to the session as well as the SID, because a parent who
/// authenticated at the kitchen computer has not authorized anything in
/// somebody else's remote session under the same account.
///
/// IN MEMORY, ON PURPOSE
/// ---------------------
/// Capabilities do not survive a service restart. That is the conservative
/// direction: a restarted service has forgotten that anyone is a parent, and
/// the parent authenticates again.
/// </summary>
public sealed class ParentCapabilityRegistry
{
    private readonly Dictionary<string, Entry> _issued = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly Lock _gate = new();

    public ParentCapabilityRegistry(TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime ?? BrokerEndpoint.ParentCapabilityLifetime;
    }

    public TimeSpan Lifetime => _lifetime;

    /// <summary>
    /// Issues a capability to a caller the service has just verified.
    ///
    /// Any capability the same caller already held is dropped. One parent
    /// authentication means one capability; letting them accumulate would
    /// turn repeated honest unlocks into a growing set of live authorities
    /// nobody is tracking.
    /// </summary>
    public string Issue(BrokerCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        lock (_gate)
        {
            Prune();

            foreach (var stale in _issued
                         .Where(e => e.Value.Matches(caller))
                         .Select(e => e.Key)
                         .ToArray())
            {
                _issued.Remove(stale);
            }

            _issued[token] = new Entry(caller.Sid, caller.SessionId, _time.GetUtcNow() + _lifetime);
        }

        return token;
    }

    /// <summary>
    /// Whether this exact value was issued to this exact caller and still
    /// stands.
    ///
    /// A missing, expired, unknown or mismatched value is false, and false is
    /// a refusal rather than a downgrade.
    /// </summary>
    public bool IsValid(string? token, BrokerCaller caller)
    {
        if (string.IsNullOrEmpty(token) || caller is null || !caller.IsAuthenticated)
        {
            return false;
        }

        lock (_gate)
        {
            Prune();

            return _issued.TryGetValue(token, out var entry) && entry.Matches(caller);
        }
    }

    /// <summary>Withdraws one capability, when a parent session ends.</summary>
    public void Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        lock (_gate)
        {
            _issued.Remove(token);
        }
    }

    /// <summary>Withdraws every capability. Used when the policy changes.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            _issued.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _issued.Count;
            }
        }
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();

        foreach (var expired in _issued
                     .Where(e => e.Value.ExpiresUtc <= now)
                     .Select(e => e.Key)
                     .ToArray())
        {
            _issued.Remove(expired);
        }
    }

    private sealed record Entry(string Sid, int SessionId, DateTimeOffset ExpiresUtc)
    {
        public bool Matches(BrokerCaller caller) =>
            string.Equals(Sid, caller.Sid, StringComparison.OrdinalIgnoreCase) &&
            SessionId == caller.SessionId;
    }
}
