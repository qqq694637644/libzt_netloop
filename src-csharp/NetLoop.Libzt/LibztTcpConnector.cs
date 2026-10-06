using System.Net;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnector : IProxyConnector
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private const int AttemptTimeoutMilliseconds = 500;

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

        var deadline = DateTimeOffset.UtcNow + _connectTimeout;
        var attempt = 0;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var fd = LibztNative.Socket(family, LibztNative.SockStream, 0);
            if (fd < 0)
            {
                lastError = new LibztException(
                    "zts_bsd_socket",
                    fd,
                    LibztNative.GetErrno());
            }
            else
            {
                try
                {
                    ConfigureStream(fd);
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    var attemptTimeout = Math.Clamp(
                        (int)Math.Max(250, remaining.TotalMilliseconds),
                        250,
                        AttemptTimeoutMilliseconds);

                    // zts_connect() retries on the same fd. After a physical
                    // network rebind that fd may already carry stale lwIP path
                    // state, so bound each native attempt to 500 ms and replace
                    // the fd before retrying. Cancellation is observed after
                    // the bounded native attempt; it never races shutdown()
                    // against connect() on the same lwIP socket.
                    var result = await Task.Run(
                        () => LibztNative.ConnectEasy(
                            fd,
                            target.Host,
                            target.Port,
                            attemptTimeout),
                        CancellationToken.None).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (result == LibztNative.Ok)
                    {
                        var nonBlocking = LibztNative.SetBlocking(fd, 0);
                        if (nonBlocking != LibztNative.Ok)
                        {
                            throw new LibztException(
                                "zts_set_blocking(connected)",
                                nonBlocking,
                                LibztNative.GetLastSocketError(fd));
                        }

                        if (attempt > 1)
                        {
                            JsonLog.Info("libzt_connect_recovered", new {
                                target = target.ToString(),
                                attempts = attempt
                            });
                        }

                        return new LibztTcpConnection(
                            fd,
                            $"libzt:{target}");
                    }

                    lastError = new LibztException(
                        "zts_connect",
                        result,
                        GetSocketError(fd));
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
                    _ = LibztNative.Close(fd);
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
                _ = LibztNative.Close(fd);
            }

            JsonLog.Info("libzt_connect_retry", new {
                target = target.ToString(),
                attempts = attempt,
                error = lastError?.Message
            });

            var delay = deadline - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
                break;

            await Task.Delay(
                delay < RetryDelay ? delay : RetryDelay,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"libzt connect timed out for {target}",
            lastError);
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

    private static int GetSocketError(int fd)
        => LibztNative.GetLastSocketError(fd);
}
