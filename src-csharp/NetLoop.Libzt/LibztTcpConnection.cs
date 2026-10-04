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

    public ValueTask<int> ReadAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        return new ValueTask<int>(Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested();
            var fd = GetFd();
            using var registration = cancellationToken.Register(
                static state => LibztNative.Shutdown((int)state!, LibztNative.ShutReadWrite),
                fd);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var result = LibztNative.Read(fd, handle.AddrOfPinnedObject(), checked((uint)count));
                if (result >= 0)
                    return result;

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new LibztException("zts_bsd_read", result, LibztNative.GetErrno());
            }
            finally
            {
                handle.Free();
            }
        }));
    }

    public ValueTask WriteAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        return new ValueTask(Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested();
            var fd = GetFd();
            using var registration = cancellationToken.Register(
                static state => LibztNative.Shutdown((int)state!, LibztNative.ShutReadWrite),
                fd);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var basePointer = handle.AddrOfPinnedObject();
                var offset = 0;
                while (offset < count)
                {
                    var requested = count - offset;
                    var result = LibztNative.Write(
                        fd,
                        basePointer + offset,
                        checked((uint)requested));

                    if (result <= 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(cancellationToken);
                        throw new LibztException("zts_bsd_write", result, LibztNative.GetErrno());
                    }

                    offset += result;
                }
            }
            finally
            {
                handle.Free();
            }
        }));
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
