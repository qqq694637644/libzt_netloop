using System.Net;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnector : IProxyConnector
{
    private readonly TimeSpan _connectTimeout;

    public LibztTcpConnector(TimeSpan connectTimeout)
    {
        _connectTimeout = connectTimeout;
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target.Host, out var address))
            throw new ArgumentException("libzt peer target must be a Managed IP address.", nameof(target));

        var family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? LibztNative.AfInet
            : LibztNative.AfInet6;

        var fd = LibztNative.Socket(family, LibztNative.SockStream, 0);
        if (fd < 0)
            throw new LibztException("zts_bsd_socket", fd, LibztNative.GetErrno());

        try
        {
            ConfigureStream(fd);
            cancellationToken.ThrowIfCancellationRequested();
            var timeoutMs = checked((int)Math.Clamp(
                Math.Ceiling(_connectTimeout.TotalMilliseconds),
                1,
                int.MaxValue));

            // zts_connect() is already the libzt convenience API that retries
            // internally while a transport-triggered peer path is forming.
            // Do not wrap it in another native fresh-socket retry loop.
            using var registration = cancellationToken.Register(
                static state =>
                    LibztNative.Shutdown(
                        (int)state!,
                        LibztNative.ShutReadWrite),
                fd);
            var result = await Task.Run(
                () => LibztNative.ConnectEasy(fd, target.Host, target.Port, timeoutMs),
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (result != LibztNative.Ok)
            {
                var error = new LibztException(
                    "zts_connect",
                    result,
                    LibztNative.GetErrno());
                throw new TimeoutException(
                    $"libzt connect timed out for {target}",
                    error);
            }

            return new LibztTcpConnection(fd, $"libzt:{target}");
        }
        catch
        {
            _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
            _ = LibztNative.Close(fd);
            throw;
        }
    }

    internal static void ConfigureStream(int fd)
    {
        ThrowSocketError("zts_set_no_delay", fd, LibztNative.SetNoDelay(fd, 1));
        ThrowSocketError("zts_set_keepalive", fd, LibztNative.SetKeepAlive(fd, 1));
        SetTcpOption(fd, LibztNative.TcpKeepIdle, 5, "TCP_KEEPIDLE");
        SetTcpOption(fd, LibztNative.TcpKeepInterval, 2, "TCP_KEEPINTVL");
        SetTcpOption(fd, LibztNative.TcpKeepCount, 3, "TCP_KEEPCNT");
    }

    private static void SetTcpOption(int fd, int option, int value, string name)
    {
        unsafe
        {
            var localValue = value;
            var result = LibztNative.SetSocketOption(
                fd,
                LibztNative.IpProtoTcp,
                option,
                (nint)(&localValue),
                checked((ushort)sizeof(int)));
            ThrowSocketError($"zts_bsd_setsockopt({name})", fd, result);
        }
    }

    private static void ThrowSocketError(string operation, int fd, int result)
    {
        if (result != LibztNative.Ok)
            throw new LibztException(operation, result, LibztNative.GetLastSocketError(fd));
    }
}
