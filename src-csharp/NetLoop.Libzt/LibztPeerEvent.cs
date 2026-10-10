using System.Runtime.InteropServices;
using System.Text;

namespace NetLoop.Libzt;

/// <summary>
/// Decodes the peer snapshot supplied by libzt's event callback. This is the
/// transport state at the time of the event, not a continuous path probe.
/// </summary>
internal static class LibztPeerEvent
{
    private static readonly int HeaderSize = Marshal.SizeOf<LibztNative.PeerInfoHeader>();
    private static readonly int PathSize = Marshal.SizeOf<LibztNative.PeerPathInfo>();

    internal static bool IsPeerEvent(short code)
        => code is >= LibztNative.EventPeerDirect and <= LibztNative.EventPeerPathDead;

    internal static object Decode(
        short code,
        ulong networkId,
        nint peerPointer,
        int payloadLength)
    {
        var change = code switch {
            LibztNative.EventPeerDirect => "PEER_DIRECT",
            LibztNative.EventPeerRelay => "PEER_RELAY",
            LibztNative.EventPeerUnreachable => "PEER_UNREACHABLE",
            LibztNative.EventPeerPathDiscovered => "PEER_PATH_DISCOVERED",
            LibztNative.EventPeerPathDead => "PEER_PATH_DEAD",
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };

        if (peerPointer == 0)
        {
            return new {
                event_code = code,
                change,
                network = networkId.ToString("x16"),
                peer_id = (string?)null,
                peer_role = (string?)null,
                transport = "UNKNOWN",
                path_count = (uint?)null,
                physical_paths = Array.Empty<object>()
            };
        }

        // The fixed layout is taken from this repo's pinned libzt header. A
        // different native peer ABI must not be silently interpreted as DIRECT.
        if (HeaderSize != 40 || PathSize != 120)
        {
            throw new InvalidDataException(
                $"Unsupported libzt peer ABI: header={HeaderSize}, path={PathSize}.");
        }
        if (payloadLength < HeaderSize)
            throw new InvalidDataException($"Truncated libzt peer event: {payloadLength} bytes.");

        var peer = Marshal.PtrToStructure<LibztNative.PeerInfoHeader>(peerPointer);
        if (peer.PeerId == 0 || peer.PeerId > 0xffffffffffUL
            || peer.PathCount > LibztNative.MaxPeerPaths)
        {
            throw new InvalidDataException(
                $"Invalid libzt peer event fields: id={peer.PeerId:x}, paths={peer.PathCount}.");
        }
        if (payloadLength < HeaderSize + peer.PathCount * PathSize)
        {
            throw new InvalidDataException(
                $"Truncated libzt peer paths: {payloadLength} bytes for {peer.PathCount} paths.");
        }

        var physicalPaths = new List<object>((int)peer.PathCount);
        for (var index = 0; index < peer.PathCount; index++)
        {
            var pointer = IntPtr.Add(peerPointer, HeaderSize + index * PathSize);
            var path = Marshal.PtrToStructure<LibztNative.PeerPathInfo>(pointer);
            physicalPaths.Add(new {
                endpoint = TryFormatEndpoint(pointer),
                preferred = path.Preferred != 0,
                expired = path.Expired != 0,
                latency_ms = float.IsFinite(path.Latency) && path.Latency >= 0
                    ? (float?)path.Latency : null
            });
        }

        // Peer events describe the ZeroTier physical underlay, not the NetLoop
        // overlay route. An unreachable event takes precedence over path count.
        var transport = code == LibztNative.EventPeerUnreachable
            ? "UNREACHABLE"
            : peer.PathCount > 0
                ? "DIRECT"
                : code == LibztNative.EventPeerDirect
                    ? "UNKNOWN"
                    : "RELAY";

        var role = peer.Role switch {
            0 => "LEAF",
            1 => "MOON",
            2 => "PLANET",
            _ => "UNKNOWN"
        };
        return new {
            event_code = code,
            change,
            network = networkId.ToString("x16"),
            peer_id = peer.PeerId.ToString("x10"),
            peer_role = role,
            transport,
            path_count = peer.PathCount,
            physical_paths = physicalPaths
        };
    }

    private static unsafe string? TryFormatEndpoint(nint sockaddr)
    {
        Span<byte> text = stackalloc byte[LibztNative.IpStringLength];
        text.Clear();
        ushort port = 0;
        int rc;
        fixed (byte* destination = text)
        {
            rc = LibztNative.SocketAddressToString(
                sockaddr,
                LibztNative.PeerSocketAddressBytes,
                (nint)destination,
                text.Length,
                ref port);
        }
        if (rc != LibztNative.Ok)
            return null;

        var length = text.IndexOf((byte)0);
        if (length <= 0)
            return null;

        var address = Encoding.ASCII.GetString(text[..length]);
        return address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
    }
}
