using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnection : IProxyConnection
{
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
                cancellationToken).ConfigureAwait(false);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var result = LibztNative.Read(fd, handle.AddrOfPinnedObject(), checked((uint)count));
                if (result >= 0)
                    return result;

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                var errno = LibztNative.GetErrno();
                if (LibztSocketPoller.IsWouldBlock(errno))
                    continue;

                throw new LibztException("zts_bsd_read", result, errno);
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
                    cancellationToken).ConfigureAwait(false);

                var requested = count - offset;
                var result = LibztNative.Write(
                    fd,
                    basePointer + offset,
                    checked((uint)requested));

                if (result < 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    var errno = LibztNative.GetErrno();
                    if (LibztSocketPoller.IsWouldBlock(errno))
                        continue;

                    throw new LibztException("zts_bsd_write", result, errno);
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

        var result = LibztNative.Shutdown(fd, LibztNative.ShutWrite);
        if (result != LibztNative.Ok && Volatile.Read(ref _fd) >= 0)
            throw new LibztException("zts_bsd_shutdown(write)", result, LibztNative.GetErrno());

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd < 0)
            return ValueTask.CompletedTask;

        _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
        _ = LibztNative.Close(fd);
        return ValueTask.CompletedTask;
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztTcpConnection));
    }
}
