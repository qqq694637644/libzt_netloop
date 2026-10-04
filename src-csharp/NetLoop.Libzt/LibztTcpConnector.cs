using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnector : IProxyConnector
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private const int ConnectPollMilliseconds = 50;
    private const int AttemptTimeoutMilliseconds = 2_000;
    private const int IoPollMicroseconds = 250_000;
    private const int SocketAddressBufferSize = 128;

    private readonly TimeSpan _connectTimeout;

    public LibztTcpConnector(TimeSpan connectTimeout)
    {
        _connectTimeout = connectTimeout;
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
        => await ConnectForGenerationAsync(
            target,
            CancellationToken.None,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<LibztTcpConnection> ConnectForGenerationAsync(
        ProxyTarget target,
        CancellationToken generationToken,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target.Host, out var address))
            throw new ArgumentException("libzt peer target must be a Managed IP address.", nameof(target));

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationToken);
        var token = linkedCts.Token;

        var family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? LibztNative.AfInet
            : LibztNative.AfInet6;

        var deadline = DateTimeOffset.UtcNow + _connectTimeout;
        var attempt = 0;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            attempt++;

            var fd = LibztNative.Socket(family, LibztNative.SockStream, 0);
            if (fd < 0)
            {
                lastError = new LibztException("zts_bsd_socket", fd, LibztNative.GetErrno());
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

                    await ConnectNonBlockingAsync(
                        fd,
                        target,
                        attemptTimeout,
                        token).ConfigureAwait(false);

                    if (attempt > 1)
                        JsonLog.Info("libzt_connect_recovered", new { target = target.ToString(), attempts = attempt });

                    var connection = new LibztTcpConnection(fd, $"libzt:{target}");
                    connection.BindGeneration(generationToken);
                    return connection;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
                _ = LibztNative.Close(fd);
            }

            if (attempt == 1)
                JsonLog.Info("libzt_connect_retry", new { target = target.ToString(), error = lastError?.Message });

            var delay = deadline - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
                break;

            await Task.Delay(delay < RetryDelay ? delay : RetryDelay, token).ConfigureAwait(false);
        }

        throw new TimeoutException($"libzt connect timed out for {target}", lastError);
    }

    private static async Task ConnectNonBlockingAsync(
        int fd,
        ProxyTarget target,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowSocketError(
            "zts_set_blocking(connect)",
            fd,
            LibztNative.SetBlocking(fd, 0));

        var address = Marshal.AllocHGlobal(SocketAddressBufferSize);
        var pollPointer = Marshal.AllocHGlobal(Marshal.SizeOf<LibztNative.PollDescriptor>());
        try
        {
            uint addressLength = SocketAddressBufferSize;
            var convert = LibztNative.IpStringToSocketAddress(
                target.Host,
                target.Port,
                address,
                ref addressLength);
            if (convert != LibztNative.Ok)
            {
                throw new LibztException(
                    "zts_util_ipstr_to_saddr",
                    convert,
                    LibztNative.GetErrno());
            }
            if (addressLength > ushort.MaxValue)
                throw new InvalidOperationException($"libzt sockaddr length is invalid: {addressLength}");

            var started = Stopwatch.GetTimestamp();
            var result = LibztNative.Connect(
                fd,
                address,
                checked((ushort)addressLength));
            if (result != LibztNative.Ok)
            {
                var errno = LibztNative.GetErrno();
                if (!IsConnectPending(errno))
                    throw new LibztException("zts_bsd_connect", result, errno);
            }

            while (result != LibztNative.Ok)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = Stopwatch.GetElapsedTime(started);
                var remaining = TimeSpan.FromMilliseconds(timeoutMilliseconds) - elapsed;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException($"libzt non-blocking connect timed out for {target}");

                var waitMilliseconds = Math.Clamp(
                    (int)Math.Ceiling(remaining.TotalMilliseconds),
                    1,
                    ConnectPollMilliseconds);
                var poll = new LibztNative.PollDescriptor {
                    FileDescriptor = fd,
                    Events = (short)(LibztNative.PollOut | LibztNative.PollErr),
                    ReturnedEvents = 0
                };
                Marshal.StructureToPtr(poll, pollPointer, false);
                var ready = LibztNative.Poll(
                    pollPointer,
                    1,
                    waitMilliseconds);
                if (ready < 0)
                {
                    throw new LibztException(
                        "zts_bsd_poll(connect)",
                        ready,
                        LibztNative.GetErrno());
                }
                if (ready == 0)
                    continue;

                poll = Marshal.PtrToStructure<LibztNative.PollDescriptor>(pollPointer);
                if ((poll.ReturnedEvents & LibztNative.PollNval) != 0)
                    throw new IOException($"libzt connect poll reported POLLNVAL for {target}");

                result = LibztNative.Connect(
                    fd,
                    address,
                    checked((ushort)addressLength));
                if (result == LibztNative.Ok)
                    break;

                var errno = LibztNative.GetErrno();
                if (errno == LibztNative.EIsConn)
                {
                    result = LibztNative.Ok;
                    break;
                }
                if (IsConnectPending(errno))
                    continue;

                throw new LibztException("zts_bsd_connect", result, errno);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowSocketError(
                "zts_set_blocking(connected)",
                fd,
                LibztNative.SetBlocking(fd, 1));
        }
        finally
        {
            Marshal.FreeHGlobal(pollPointer);
            Marshal.FreeHGlobal(address);
        }
    }

    private static bool IsConnectPending(int errno)
        => errno is LibztNative.EAgain
            or LibztNative.EAlready
            or LibztNative.EInProgress
            or LibztNative.WindowsEWouldBlock;

    internal static void ConfigureStream(int fd)
    {
        ThrowSocketError("zts_set_no_delay", fd, LibztNative.SetNoDelay(fd, 1));
        ThrowSocketError("zts_set_keepalive", fd, LibztNative.SetKeepAlive(fd, 1));
        ThrowSocketError(
            "zts_set_recv_timeout",
            fd,
            LibztNative.SetReceiveTimeout(fd, 0, IoPollMicroseconds));
        ThrowSocketError(
            "zts_set_send_timeout",
            fd,
            LibztNative.SetSendTimeout(fd, 0, IoPollMicroseconds));
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
