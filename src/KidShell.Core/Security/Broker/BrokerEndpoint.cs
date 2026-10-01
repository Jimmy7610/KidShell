namespace KidShell.Core.Security.Broker;

/// <summary>
/// Where the privileged broker listens, and the limits of the conversation.
///
/// THE CLIENT NEVER CHOOSES ANY OF THIS
/// ------------------------------------
/// The pipe name is a compile-time constant on both sides. A client that
/// could name its endpoint could be pointed at a pipe somebody else created
/// and had its requests - and the parent's policy - answered by whoever
/// created it. A server that took its name from configuration would be a
/// LocalSystem service listening wherever a writable file said.
/// </summary>
public static class BrokerEndpoint
{
    /// <summary>
    /// The one pipe. Versioned in the name, so a future incompatible protocol
    /// is a different endpoint rather than a negotiation.
    /// </summary>
    public const string PipeName = "KidShell.Security.v1";

    /// <summary>
    /// The protocol the two sides must agree on.
    ///
    /// Carried in every request and checked before anything else is read. A
    /// mismatch is rejected rather than tolerated: a privileged side that
    /// guesses at an unfamiliar message shape has stopped being a boundary.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The largest request the server will read, in bytes.
    ///
    /// Enforced by the framing before deserialisation, so a caller cannot
    /// make a LocalSystem service allocate a gigabyte by announcing one. The
    /// payload cap inside is smaller still; this is the envelope.
    /// </summary>
    public const int MaxRequestBytes = 512 * 1024;

    /// <summary>The largest response the client will read.</summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// How long the client waits for the service to accept a connection.
    ///
    /// Short. A missing or stopped service must surface as "enforcement is
    /// unavailable" quickly, because the product's answer to that is to fail
    /// closed, and a child staring at a frozen shell is not failing closed.
    /// </summary>
    public const int ConnectTimeoutMilliseconds = 2_000;

    /// <summary>How long either side waits for the other to finish a message.</summary>
    public const int IoTimeoutMilliseconds = 10_000;

    /// <summary>
    /// How long a parent capability lives once the service has issued one.
    ///
    /// Long enough for a parent to finish adjusting settings, short enough
    /// that a shell left unlocked on a kitchen table stops being an authority
    /// before the child comes back. The parent session's own inactivity
    /// timeout is five minutes shorter, so in ordinary use the UI relocks
    /// first and this is the backstop.
    /// </summary>
    public static readonly TimeSpan ParentCapabilityLifetime = TimeSpan.FromMinutes(25);
}
