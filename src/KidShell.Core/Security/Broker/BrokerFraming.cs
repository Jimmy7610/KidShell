using System.Buffers.Binary;
using System.Text;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// How one message is put on the wire and taken off it.
///
/// A four-byte big-endian length, then that many bytes of UTF-8 JSON.
///
/// WHY NOT A LINE
/// --------------
/// The old transport read a line from a pipe and called it a message. Reading
/// until a newline means the receiver does not know how much it is about to
/// read until it has read it, and the receiver here is a LocalSystem service.
/// A caller that opens the pipe and sends four gigabytes without a newline
/// should be refused at the fourth byte, not at the fourth gigabyte.
///
/// So the length comes first and is checked against the limit BEFORE a buffer
/// is allocated for it. The limit is the protocol's, not the sender's.
/// </summary>
public static class BrokerFraming
{
    public const int PrefixBytes = 4;

    /// <summary>Turns a message into its complete wire form.</summary>
    public static byte[] Frame(string message, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = Encoding.UTF8.GetBytes(message);

        if (payload.Length > maxBytes)
        {
            throw new InvalidOperationException(
                $"A broker message of {payload.Length} bytes exceeds the {maxBytes} byte limit.");
        }

        var buffer = new byte[PrefixBytes + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length);
        payload.CopyTo(buffer, PrefixBytes);

        return buffer;
    }

    /// <summary>
    /// Reads one message, refusing anything the protocol does not allow.
    ///
    /// Returns null when the stream ends cleanly before a message, which is
    /// an ordinary disconnection rather than an error.
    /// </summary>
    public static async Task<string?> ReadAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[PrefixBytes];

        if (!await FillAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);

        if (length < 0 || length > maxBytes)
        {
            // Nothing is allocated for it, and the conversation is over. A
            // caller that announces a message this service will not read has
            // not earned another attempt on the same connection.
            throw new BrokerFrameException(
                $"A broker message announced {length} bytes against a {maxBytes} byte limit.");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var payload = new byte[length];

        if (!await FillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new BrokerFrameException("A broker message ended before its announced length.");
        }

        return Encoding.UTF8.GetString(payload);
    }

    public static async Task WriteAsync(
        Stream stream, string message, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var framed = Frame(message, maxBytes);

        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads exactly as many bytes as the buffer holds, or reports the end.</summary>
    private static async Task<bool> FillAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream
                .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }
}

/// <summary>A message that did not obey the framing rules.</summary>
public sealed class BrokerFrameException : Exception
{
    public BrokerFrameException(string message) : base(message)
    {
    }
}
