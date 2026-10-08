using System.Globalization;
using System.Net;
using System.Text;
using NetLoop.Core;

namespace NetLoop.Socks;

public readonly record struct Socks5UdpPacket(ProxyTarget Target, ReadOnlyMemory<byte> Content)
{
    public static bool TryParse(
        ReadOnlyMemory<byte> datagram,
        out Socks5UdpPacket packet,
        out string? error)
    {
        packet = default;
        error = null;
        var span = datagram.Span;
        if (span.Length < 7)
        {
            error = "datagram is too short";
            return false;
        }

        if (span[0] != 0 || span[1] != 0)
        {
            error = "reserved bytes are non-zero";
            return false;
        }

        if (span[2] != 0)
        {
            error = "FRAG != 0 is not supported";
            return false;
        }

        var offset = 4;
        string host;
        switch (span[3])
        {
            case Socks5Protocol.Ipv4:
                if (span.Length < offset + 4 + 2)
                {
                    error = "truncated IPv4 target";
                    return false;
                }

                host = new IPAddress(span.Slice(offset, 4)).ToString();
                offset += 4;
                break;

            case Socks5Protocol.Ipv6:
                if (span.Length < offset + 16 + 2)
                {
                    error = "truncated IPv6 target";
                    return false;
                }

                host = new IPAddress(span.Slice(offset, 16)).ToString();
                offset += 16;
                break;

            case Socks5Protocol.Domain:
                if (span.Length < offset + 1)
                {
                    error = "truncated domain length";
                    return false;
                }

                var length = span[offset++];
                if (length == 0 || span.Length < offset + length + 2)
                {
                    error = "invalid or truncated domain target";
                    return false;
                }

                try
                {
                    host = new IdnMapping().GetUnicode(Encoding.ASCII.GetString(span.Slice(offset, length)));
                }
                catch (ArgumentException)
                {
                    error = "invalid IDN domain target";
                    return false;
                }

                offset += length;
                break;

            default:
                error = $"unsupported address type {span[3]}";
                return false;
        }

        var port = checked((ushort)((span[offset] << 8) | span[offset + 1]));
        offset += 2;
        if (port == 0)
        {
            error = "target port is zero";
            return false;
        }

        packet = new Socks5UdpPacket(new ProxyTarget(host, port), datagram[offset..]);
        return true;
    }

    public static byte[] Build(ProxyTarget source, ReadOnlySpan<byte> content)
    {
        var request = Socks5Protocol.BuildTargetRequest(0, source);
        // BuildTargetRequest emits VER/CMD/RSV/ATYP. SOCKS UDP needs RSV/FRAG/ATYP,
        // so drop VER and reuse the encoded ATYP/address/port.
        var result = new byte[3 + (request.Length - 3) + content.Length];
        result[0] = 0;
        result[1] = 0;
        result[2] = 0;
        request.AsSpan(3).CopyTo(result.AsSpan(3));
        content.CopyTo(result.AsSpan(request.Length));
        return result;
    }
}
