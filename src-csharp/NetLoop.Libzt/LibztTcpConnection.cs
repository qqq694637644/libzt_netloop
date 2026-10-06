using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnection : IProxyConnection
{
    // The pinned lwIP build has LWIP_NETCONN_FULLDUPLEX=0, so native socket
    // operations on one fd must not overlap across the two relay pumps.
    private readonly object _nativeGate = new();
    private int _fd;

    internal LibztTcpConnection(int fd, string description)
    {
        _fd = fd;
        Description = description;
    }

    public string Description { get; }

    public System.Net.EndPoint? LocalEndPoint => null;

    public System.Net.EndPoint? RemoteEndPoint => null;

    public async ValueTask<int> ReadAsync(
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fd = GetFd();
            await LibztSocketPoller.WaitAsync(
                fd,
                LibztNative.PollIn,
                cancellationToken,
                _nativeGate,
                GetCurrentFd,
                nameof(LibztTcpConnection)).ConfigureAwait(false);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int result;
                int socketError = 0;
                lock (_nativeGate)
                {
                    EnsureCurrentFd(fd, cancellationToken);
                    result = LibztNative.Read(
                        fd,
                        handle.AddrOfPinnedObject(),
                        checked((uint)count));
                    if (result == LibztNative.ErrSocket)
                        socketError = LibztNative.GetLastSocketError(fd);
                }

                if (result >= 0)
                    return result;

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                if (result == LibztNative.ErrSocket)
                {
                    // zts_errno is process-global in the pinned libzt/lwIP
                    // build, so it cannot identify which concurrent fd failed.
                    // A clean per-fd SO_ERROR after a nonblocking read is a
                    // readiness race; poll again. Real TCP errors keep POLLERR
                    // asserted and are handled by the next poll.
                    if (socketError == 0
                        || LibztSocketPoller.IsWouldBlock(socketError))
                    {
                        continue;
                    }

                    throw new LibztException(
                        "zts_bsd_read",
                        result,
                        socketError);
                }

                throw new IOException(
                    $"zts_bsd_read failed: api_rc={result}");
            }
            finally
            {
                handle.Free();
            }
        }
    }

    public async ValueTask WriteAsync(
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var basePointer = handle.AddrOfPinnedObject();
            var offset = 0;
            while (offset < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fd = GetFd();
                await LibztSocketPoller.WaitAsync(
                    fd,
                    LibztNative.PollOut,
                    cancellationToken,
                    _nativeGate,
                    GetCurrentFd,
                    nameof(LibztTcpConnection)).ConfigureAwait(false);

                var requested = count - offset;
                int result;
                int socketError = 0;
                lock (_nativeGate)
                {
                    EnsureCurrentFd(fd, cancellationToken);
                    result = LibztNative.Write(
                        fd,
                        basePointer + offset,
                        checked((uint)requested));
                    if (result == LibztNative.ErrSocket)
                        socketError = LibztNative.GetLastSocketError(fd);
                }

                if (result < 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    if (result == LibztNative.ErrSocket)
                    {
                        if (socketError == 0
                            || LibztSocketPoller.IsWouldBlock(socketError))
                        {
                            continue;
                        }

                        throw new LibztException(
                            "zts_bsd_write",
                            result,
                            socketError);
                    }

                    throw new IOException(
                        $"zts_bsd_write failed: api_rc={result}");
                }

                if (result == 0)
                    throw new IOException("libzt TCP write returned zero.");

                offset += result;
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public ValueTask ShutdownWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fd = Volatile.Read(ref _fd);
        if (fd < 0)
            return ValueTask.CompletedTask;

        int result;
        int socketError;
        lock (_nativeGate)
        {
            EnsureCurrentFd(fd, cancellationToken);
            result = LibztNative.Shutdown(fd, LibztNative.ShutWrite);
            socketError = result == LibztNative.Ok
                ? 0
                : LibztNative.GetLastSocketError(fd);
        }
        if (result != LibztNative.Ok && Volatile.Read(ref _fd) >= 0)
        {
            throw new LibztException(
                "zts_bsd_shutdown(write)",
                result,
                socketError);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_nativeGate)
        {
            var fd = Interlocked.Exchange(ref _fd, -1);
            if (fd < 0)
                return ValueTask.CompletedTask;

            _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
            _ = LibztNative.Close(fd);
        }
        return ValueTask.CompletedTask;
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztTcpConnection));
    }

    private int GetCurrentFd()
        => Volatile.Read(ref _fd);

    private void EnsureCurrentFd(
        int expectedFd,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _fd) == expectedFd)
            return;

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        throw new ObjectDisposedException(nameof(LibztTcpConnection));
    }
}
