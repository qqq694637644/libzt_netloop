using System.Globalization;
using System.Net;
using System.Text;
using NetLoop.Core;

namespace NetLoop.Socks;

internal static class Socks5Protocol
{
    internal const byte Version = 5;
    internal const byte NoAuthentication = 0;
    internal const byte UsernamePassword = 2;
    internal const byte NoAcceptableMethods = 0xFF;
    internal const byte Connect = 1;
    internal const byte UdpAssociate = 3;
    internal const byte Ipv4 = 1;
    internal const byte Domain = 3;
    internal const byte Ipv6 = 4;

    internal const byte ReplySucceeded = 0;
    internal const byte ReplyGeneralFailure = 1;
    internal const byte ReplyNetworkUnreachable = 3;
    internal const byte ReplyHostUnreachable = 4;
    internal const byte ReplyConnectionRefused = 5;
    internal const byte ReplyTtlExpired = 6;
    internal const byte ReplyCommandNotSupported = 7;
    internal const byte ReplyAddressTypeNotSupported = 8;

    internal static async ValueTask ReadExactlyAsync(
        IProxyConnection connection,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < count)
        {
            var scratch = offset == 0 ? buffer : new byte[count - offset];
            var read = await connection.ReadAsync(scratch, count - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Unexpected EOF during SOCKS5 handshake.");

            if (offset != 0)
                Buffer.BlockCopy(scratch, 0, buffer, offset, read);
            offset += read;
        }
    }

    internal static async ValueTask<ProxyTarget> ReadTargetAsync(
        IProxyConnection connection,
        byte addressType,
        CancellationToken cancellationToken,
        bool allowZeroPort = false)
    {
        string host;
        switch (addressType)
        {
            case Ipv4:
            {
                var bytes = new byte[4];
                await ReadExactlyAsync(connection, bytes, bytes.Length, cancellationToken).ConfigureAwait(false);
                host = new IPAddress(bytes).ToString();
                break;
            }
            case Ipv6:
            {
                var bytes = new byte[16];
                await ReadExactlyAsync(connection, bytes, bytes.Length, cancellationToken).ConfigureAwait(false);
                host = new IPAddress(bytes).ToString();
                break;
            }
            case Domain:
            {
                var length = new byte[1];
                await ReadExactlyAsync(connection, length, 1, cancellationToken).ConfigureAwait(false);
                if (length[0] == 0)
                    throw new ProtocolViolationException("SOCKS5 domain cannot be empty.");

                var bytes = new byte[length[0]];
                await ReadExactlyAsync(connection, bytes, bytes.Length, cancellationToken).ConfigureAwait(false);
                host = Encoding.ASCII.GetString(bytes);
                break;
            }
            default:
                throw new NotSupportedException($"SOCKS5 address type {addressType} is not supported.");
        }

        var portBytes = new byte[2];
        await ReadExactlyAsync(connection, portBytes, 2, cancellationToken).ConfigureAwait(false);
        var port = checked((ushort)((portBytes[0] << 8) | portBytes[1]));
        if (port == 0 && !allowZeroPort)
            throw new ProtocolViolationException("SOCKS5 target port cannot be zero.");

        return new ProxyTarget(host, port);
    }

    internal static byte[] BuildTargetRequest(byte command, ProxyTarget target)
    {
        var bytes = new List<byte>(32) { Version, command, 0 };

        if (IPAddress.TryParse(target.Host, out var ip))
        {
            var raw = ip.GetAddressBytes();
            bytes.Add(raw.Length == 4 ? Ipv4 : Ipv6);
            bytes.AddRange(raw);
        }
        else
        {
            var ascii = new IdnMapping().GetAscii(target.Host);
            var raw = Encoding.ASCII.GetBytes(ascii);
            if (raw.Length is 0 or > 255)
                throw new ArgumentException("SOCKS5 domain length must be 1..255 bytes.", nameof(target));

            bytes.Add(Domain);
            bytes.Add(checked((byte)raw.Length));
            bytes.AddRange(raw);
        }

        bytes.Add((byte)(target.Port >> 8));
        bytes.Add((byte)(target.Port & 0xFF));
        return bytes.ToArray();
    }

    internal static byte[] BuildReply(byte reply)
        => [Version, reply, 0, Ipv4, 0, 0, 0, 0, 0, 0];

    internal static byte[] BuildReply(byte reply, IPEndPoint endpoint)
    {
        var addressBytes = endpoint.Address.GetAddressBytes();
        var result = new byte[4 + addressBytes.Length + 2];
        result[0] = Version;
        result[1] = reply;
        result[2] = 0;
        result[3] = addressBytes.Length == 4 ? Ipv4 : Ipv6;
        addressBytes.CopyTo(result, 4);
        result[^2] = (byte)(endpoint.Port >> 8);
        result[^1] = (byte)(endpoint.Port & 0xFF);
        return result;
    }

    internal static async ValueTask ConsumeReplyAddressAsync(
        IProxyConnection connection,
        byte addressType,
        CancellationToken cancellationToken)
    {
        switch (addressType)
        {
            case Ipv4:
                await ReadExactlyAsync(connection, new byte[4], 4, cancellationToken).ConfigureAwait(false);
                break;
            case Ipv6:
                await ReadExactlyAsync(connection, new byte[16], 16, cancellationToken).ConfigureAwait(false);
                break;
            case Domain:
            {
                var length = new byte[1];
                await ReadExactlyAsync(connection, length, 1, cancellationToken).ConfigureAwait(false);
                await ReadExactlyAsync(connection, new byte[length[0]], length[0], cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new ProtocolViolationException($"Invalid SOCKS5 reply address type {addressType}.");
        }

        await ReadExactlyAsync(connection, new byte[2], 2, cancellationToken).ConfigureAwait(false);
    }
}
