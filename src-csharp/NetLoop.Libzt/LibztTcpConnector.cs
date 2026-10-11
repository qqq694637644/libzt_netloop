using System.Diagnostics;
using System.Net;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnector : IProxyConnector
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMilliseconds(1_250);

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

        var started = Stopwatch.GetTimestamp();
        var attempt = 0;
        Exception? lastError = null;

        while (Stopwatch.GetElapsedTime(started) < _connectTimeout)
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
                    var nonBlocking = LibztNative.SetBlocking(fd, 0);
                    if (nonBlocking != LibztNative.Ok)
                    {
                        throw new LibztException(
                            "zts_set_blocking(connect)",
                            nonBlocking,
                            LibztNative.GetLastSocketError(fd));
                    }

                    // The pinned zts_connect(timeout_ms) calls a *blocking*
                    // lwIP connect, and its timeout is not a deadline (even
                    // when timeout_ms < 1000). Start a nonblocking connect
                    // explicitly; never hand a pending fd to the SOCKS layer.
                    if (!BeginNonBlockingConnect(fd, target))
                    {
                        var remaining = _connectTimeout - Stopwatch.GetElapsedTime(started);
                        if (remaining <= TimeSpan.Zero)
                            throw new TimeoutException("libzt connect deadline expired.");

                        await WaitForConnectAsync(
                            fd,
                            remaining < AttemptWindow ? remaining : AttemptWindow,
                            cancellationToken).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (attempt > 1)
                    {
                        JsonLog.Info("libzt_connect_recovered", new {
                            target = target.ToString(),
                            attempts = attempt,
                            elapsed_ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                        });
                    }

                    // The socket is already nonblocking, as required by
                    // LibztTcpConnection. Transfer ownership exactly once.
                    var connectedFd = fd;
                    fd = -1;
                    return new LibztTcpConnection(connectedFd, $"libzt:{target}");
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    JsonLog.Info("libzt_connect_cancelled", new {
                        target = target.ToString(),
                        attempts = attempt,
                        elapsed_ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
                finally
                {
                    // No native call remains in flight when cancellation
                    // closes this fd; do not shutdown a connecting socket.
                    if (fd >= 0)
                        _ = LibztNative.Close(fd);
                }
            }

            JsonLog.Info("libzt_connect_retry", new {
                target = target.ToString(),
                attempts = attempt,
                elapsed_ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                error = lastError?.Message
            });

            var delay = _connectTimeout - Stopwatch.GetElapsedTime(started);
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

    private static unsafe bool BeginNonBlockingConnect(int fd, ProxyTarget target)
    {
        Span<byte> socketAddress = stackalloc byte[LibztNative.SocketAddressStorageBytes];
        socketAddress.Clear();
        uint length = (uint)socketAddress.Length;
        fixed (byte* address = socketAddress)
        {
            var convert = LibztNative.IpStringToSocketAddress(
                target.Host, target.Port, (nint)address, ref length);
            if (convert != LibztNative.Ok)
                throw new LibztException("zts_util_ipstr_to_saddr", convert);
            if (length != 16 && length != (uint)socketAddress.Length)
                throw new IOException($"Invalid libzt socket address length: {length}.");

            var result = LibztNative.ConnectSocket(fd, (nint)address, length);
            if (result == LibztNative.Ok)
                return true;
            if (result == LibztNative.ErrSocket)
            {
                // EINPROGRESS is expected. zts_errno is process-global in
                // this libzt build, so it cannot reliably distinguish a
                // concurrent socket's immediate error. Poll this fd and
                // inspect its own SO_ERROR instead.
                return false;
            }

            throw new LibztException("zts_bsd_connect", result);
        }
    }

    private static async Task WaitForConnectAsync(
        int fd,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new LibztNative.PollFd {
                Fd = fd,
                Events = LibztNative.PollOut,
                Revents = 0
            };

            // poll(0) never blocks the caller. POLLOUT alone is not enough:
            // a refused TCP connect can also become writable, so use SO_ERROR.
            var result = LibztNative.Poll(ref descriptor, 1, 0);
            if (result < 0)
            {
                throw new LibztException(
                    "zts_bsd_poll(connect)",
                    result,
                    LibztNative.GetLastSocketError(fd));
            }

            var revents = descriptor.Revents;
            if (result > 0 && (revents
                    & (LibztNative.PollOut | LibztNative.PollError | LibztNative.PollInvalid)) != 0)
            {
                var error = LibztNative.GetLastSocketError(fd);
                if (error != LibztNative.Ok)
                    throw new LibztException("zts_bsd_connect(SO_ERROR)", LibztNative.ErrSocket, error);

                if ((revents & (LibztNative.PollError | LibztNative.PollInvalid)) != 0)
                    throw new IOException($"libzt connect poll reported revents=0x{revents:x}.");

                if ((revents & LibztNative.PollOut) != 0)
                    return;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Nonblocking libzt connect exceeded {timeout.TotalMilliseconds:0} ms.");

            await Task.Delay(
                remaining < PollDelay ? remaining : PollDelay,
                cancellationToken).ConfigureAwait(false);
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
