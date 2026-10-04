using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztTcpListener : IAsyncDisposable
{
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
        var fd = GetFd();
        var accepted = await Task.Run(() => {
            var buffer = Marshal.AllocHGlobal(LibztNative.IpStringLength);
            try
            {
                ushort port = 0;
                using var registration = cancellationToken.Register(
                    static state => LibztNative.Shutdown((int)state!, LibztNative.ShutReadWrite),
                    fd);

                var result = LibztNative.AcceptEasy(fd, buffer, LibztNative.IpStringLength, ref port);
                if (result < 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    throw new LibztException("zts_bsd_accept_easy", result, LibztNative.GetLastSocketError(fd));
                }

                var remote = Marshal.PtrToStringAnsi(buffer) ?? "unknown";
                return (Socket: result, Remote: remote, Port: port);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }).ConfigureAwait(false);

        try
        {
            LibztTcpConnector.ConfigureStream(accepted.Socket);
            return new LibztTcpConnection(
                accepted.Socket,
                $"libzt-accepted:{accepted.Remote}:{accepted.Port}");
        }
        catch
        {
            _ = LibztNative.Close(accepted.Socket);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
        {
            _ = LibztNative.Shutdown(fd, LibztNative.ShutReadWrite);
            _ = LibztNative.Close(fd);
        }

        return ValueTask.CompletedTask;
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztTcpListener));
    }
}
