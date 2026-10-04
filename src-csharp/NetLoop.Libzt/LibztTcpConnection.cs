using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpConnection : IProxyConnection
{
    private CancellationTokenRegistration _generationRegistration;
    private CancellationToken _generationToken;
    private int _fd;
    private int _generationBound;

    internal LibztTcpConnection(int fd, string description)
    {
        _fd = fd;
        Description = description;
    }

    public string Description { get; }

    public System.Net.EndPoint? LocalEndPoint => null;

    public System.Net.EndPoint? RemoteEndPoint => null;

    public CancellationToken LifetimeCancellation => _generationToken;

    public void BindGeneration(CancellationToken generationToken)
    {
        if (!generationToken.CanBeCanceled)
            return;
        if (Interlocked.Exchange(ref _generationBound, 1) != 0)
            throw new InvalidOperationException("libzt connection is already bound to a network generation.");

        _generationToken = generationToken;
        _generationRegistration = generationToken.Register(
            static state => ((LibztTcpConnection)state!).AbortGeneration(),
            this);

        if (Volatile.Read(ref _fd) < 0)
            _generationRegistration.Dispose();
    }

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
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_generationToken.IsCancellationRequested)
                        throw new OperationCanceledException(_generationToken);

                    var result = LibztNative.Read(
                        fd,
                        handle.AddrOfPinnedObject(),
                        checked((uint)count));
                    if (result >= 0)
                        return result;

                    var errno = LibztNative.GetErrno();
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);
                    if (_generationToken.IsCancellationRequested)
                        throw new OperationCanceledException(_generationToken);
                    if (IsTransientIoError(errno))
                        continue;

                    throw new LibztException("zts_bsd_read", result, errno);
                }
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
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_generationToken.IsCancellationRequested)
                        throw new OperationCanceledException(_generationToken);

                    var requested = count - offset;
                    var result = LibztNative.Write(
                        fd,
                        basePointer + offset,
                        checked((uint)requested));

                    if (result <= 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(cancellationToken);
                        if (_generationToken.IsCancellationRequested)
                            throw new OperationCanceledException(_generationToken);

                        var errno = LibztNative.GetErrno();
                        if (IsTransientIoError(errno))
                            continue;

                        throw new LibztException("zts_bsd_write", result, errno);
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

    private static bool IsTransientIoError(int errno)
        => errno is LibztNative.EAgain
            or LibztNative.ETimedOut
            or LibztNative.WindowsETimedOut
            or LibztNative.WindowsEWouldBlock;

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
        _generationRegistration.Dispose();
        if (fd < 0)
            return ValueTask.CompletedTask;

        _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
        _ = LibztNative.Close(fd);
        return ValueTask.CompletedTask;
    }

    private void AbortGeneration()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd < 0)
            return;

        _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
        _ = LibztNative.Close(fd);
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztTcpConnection));
    }
}
