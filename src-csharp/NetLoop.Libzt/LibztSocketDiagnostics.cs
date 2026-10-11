using System.Text;

namespace NetLoop.Libzt;

internal static class LibztSocketDiagnostics
{
    // The pinned libzt exports the easy zts_getsockname C API. Querying the
    // local tuple on terminal attempts lets A's port match B's accept remote
    // port, without modifying the SOCKS5 wire protocol.
    internal static unsafe string? TryGetLocalEndPoint(int fd)
    {
        Span<byte> address = stackalloc byte[LibztNative.IpStringLength];
        address.Clear();
        ushort port = 0;
        int result;
        fixed (byte* text = address)
        {
            result = LibztNative.GetSocketName(
                fd, (nint)text, address.Length, ref port);
        }

        if (result != LibztNative.Ok || port == 0)
            return null;

        var length = address.IndexOf((byte)0);
        if (length <= 0)
            return null;

        var ip = Encoding.ASCII.GetString(address[..length]);
        return ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }
}
