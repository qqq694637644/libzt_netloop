using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpListener : IAsyncDisposable
{
    private readonly object _nativeGate = new();
    private int _fd;

    private LibztTcpListener(int fd, string bindAddress, ushort port)
    {
        _fd = fd;
        BindAddress = bindAddress;
        Port = port;
    }

    public string BindAddress { get; }

    public ushort Port { get; }

    public static LibztTcpListener Start(string bindAddress, ushort port, int backlog = 128)
    {
        var family = bindAddress.Contains(':') ? LibztNative.AfInet6 : LibztNative.AfInet;
        var fd = LibztNative.Socket(family, LibztNative.SockStream, 0);
        if (fd < 0)
            throw new LibztException("zts_bsd_socket(listener)", fd, LibztNative.GetErrno());

        try
        {
            var bind = LibztNative.BindEasy(fd, bindAddress, port);
            if (bind != LibztNative.Ok)
                throw new LibztException("zts_bsd_bind_easy", bind, LibztNative.GetLastSocketError(fd));

            var listen = LibztNative.Listen(fd, backlog);
            if (listen != LibztNative.Ok)
                throw new LibztException("zts_bsd_listen", listen, LibztNative.GetLastSocketError(fd));

            var nonBlocking = LibztNative.SetBlocking(fd, 0);
            if (nonBlocking != LibztNative.Ok)
                throw new LibztException(
                    "zts_set_blocking(listener)",
                    nonBlocking,
                    LibztNative.GetLastSocketError(fd));

            JsonLog.Info("libzt_tcp_listener_ready", new { address = bindAddress, port, backlog });
            return new LibztTcpListener(fd, bindAddress, port);
        }
        catch
        {
            _ = LibztNative.Close(fd);
            throw;
        }
    }

    public async ValueTask<LibztTcpConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        var buffer = Marshal.AllocHGlobal(LibztNative.IpStringLength);
        try
        {
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
                    nameof(LibztTcpListener)).ConfigureAwait(false);

                ushort port = 0;
                int result;
                int socketError = 0;
                lock (_nativeGate)
                {
                    EnsureCurrentFd(fd, cancellationToken);
                    result = LibztNative.AcceptEasy(
                        fd,
                        buffer,
                        LibztNative.IpStringLength,
                        ref port);
                    if (result == LibztNative.ErrSocket)
                        socketError = LibztNative.GetLastSocketError(fd);
                }
                if (result >= 0)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var nonBlocking = LibztNative.SetBlocking(result, 0);
                        if (nonBlocking != LibztNative.Ok)
                        {
                            throw new LibztException(
                                "zts_set_blocking(accepted)",
                                nonBlocking,
                                LibztNative.GetLastSocketError(result));
                        }

                        LibztTcpConnector.ConfigureStream(result);
                        var remote = Marshal.PtrToStringAnsi(buffer) ?? "unknown";
                        return new LibztTcpConnection(
                            result,
                            $"libzt-accepted:{remote}:{port}");
                    }
                    catch
                    {
                        _ = LibztNative.Close(result);
                        throw;
                    }
                }

                if (result == LibztNative.ErrSocket
                    && (socketError == 0
                        || LibztSocketPoller.IsWouldBlock(socketError)))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new LibztException(
                    "zts_accept",
                    result,
                    socketError);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_nativeGate)
        {
            var fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0)
            {
                _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
                _ = LibztNative.Close(fd);
            }
        }

        return ValueTask.CompletedTask;
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztTcpListener));
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

        throw new ObjectDisposedException(nameof(LibztTcpListener));
    }
}
