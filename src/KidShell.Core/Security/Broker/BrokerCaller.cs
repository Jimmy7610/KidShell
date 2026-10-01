namespace KidShell.Core.Security.Broker;

/// <summary>
/// What authority a connected caller has, as Windows reports it.
///
/// NOT AS THE CALLER REPORTS IT
/// ----------------------------
/// Every value of this type is derived by the SERVER from the connection:
/// the impersonation token on the pipe, the SID in that token, and the
/// token's elevation. Nothing in a request contributes to it. A request field
/// saying "I am the parent" would be a field saying "I am whatever I say",
/// and the privileged side would be taking its authorization from the thing
/// it is supposed to be protecting against.
/// </summary>
public enum BrokerCallerClass
{
    /// <summary>
    /// Not recognised. Every operation is refused.
    ///
    /// The default, so a resolver that fails to classify a caller fails
    /// closed rather than falling through to something permissive.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The interactive account KidShell runs as - in production, the child.
    ///
    /// Authenticated and identified, and NOT trusted with policy. This class
    /// may advance enforcement state in the stricter direction and nothing
    /// else.
    /// </summary>
    ChildSession = 1,

    /// <summary>
    /// A caller whose token is elevated and in the Administrators group.
    ///
    /// The parent, having passed a UAC prompt. The only class that may change
    /// what the policy says.
    /// </summary>
    Administrator = 2,

    /// <summary>LocalSystem. The service's own internal calls.</summary>
    System = 3
}

/// <summary>
/// One connected caller, as the server established it.
///
/// <see cref="Sid"/> comes from the impersonation token, never from JSON.
/// </summary>
public sealed record BrokerCaller
{
    public static readonly BrokerCaller Unknown = new()
    {
        Class = BrokerCallerClass.Unknown,
        Sid = string.Empty,
        AccountName = string.Empty
    };

    public required BrokerCallerClass Class { get; init; }

    /// <summary>The caller's SID, from the token on the pipe.</summary>
    public required string Sid { get; init; }

    /// <summary>The account name, for the audit log. Not used for a decision.</summary>
    public required string AccountName { get; init; }

    /// <summary>Whether the token is elevated.</summary>
    public bool IsElevated { get; init; }

    /// <summary>
    /// The Windows session the caller is in.
    ///
    /// Recorded because a capability is bound to one: a parent who
    /// authenticated at the kitchen computer has not authorized anything in
    /// somebody else's remote session.
    /// </summary>
    public int SessionId { get; init; }

    public bool IsAuthenticated => Class != BrokerCallerClass.Unknown;
}

/// <summary>
/// Establishes who is on the other end of a connection.
///
/// An interface because the implementation is Windows-only and the
/// authorization rules that consume it are not. Every test of the rules uses
/// a caller it constructed; no test of the rules needs a pipe.
/// </summary>
public interface IBrokerCallerResolver
{
    /// <summary>
    /// Classifies the currently connected caller.
    ///
    /// Returns <see cref="BrokerCaller.Unknown"/> when it cannot - which is a
    /// refusal, not a fallback.
    /// </summary>
    BrokerCaller Resolve();
}
