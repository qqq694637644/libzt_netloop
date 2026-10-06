using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed record LibztUdpDatagram(IPEndPoint RemoteEndPoint, byte[] Content);

public sealed class LibztUdpSocket : IAsyncDisposable
{
    private const int SocketAddressBufferSize = 128;
    private const int MaxDatagramSize = 65_535;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _fd;

    private LibztUdpSocket(int fd, IPAddress bindAddress, ushort bindPort)
    {
        _fd = fd;
        BindAddress = bindAddress;
        BindPort = bindPort;
    }

    public IPAddress BindAddress { get; }

    public ushort BindPort { get; }

    public static LibztUdpSocket Bind(IPAddress bindAddress, ushort bindPort)
    {
        var family = bindAddress.AddressFamily == AddressFamily.InterNetwork
            ? LibztNative.AfInet
            : LibztNative.AfInet6;

        var fd = LibztNative.Socket(family, LibztNative.SockDatagram, 0);
        if (fd < 0)
            throw new LibztException("zts_bsd_socket(udp)", fd, LibztNative.GetErrno());

        try
        {
            var bind = LibztNative.BindEasy(fd, bindAddress.ToString(), bindPort);
            if (bind != LibztNative.Ok)
                throw new LibztException("zts_bind(udp)", bind, LibztNative.GetLastSocketError(fd));

            var nonBlocking = LibztNative.SetBlocking(fd, 0);
            if (nonBlocking != LibztNative.Ok)
                throw new LibztException(
                    "zts_set_blocking(udp)",
                    nonBlocking,
                    LibztNative.GetLastSocketError(fd));

            return new LibztUdpSocket(fd, bindAddress, bindPort);
        }
        catch
        {
            _ = LibztNative.Close(fd);
            throw;
        }
    }

    public async ValueTask SendToAsync(
        ReadOnlyMemory<byte> content,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        if (content.Length > MaxDatagramSize)
            throw new ArgumentOutOfRangeException(nameof(content), "UDP datagram exceeds 65535 bytes.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = content.ToArray();
            var contentHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var address = Marshal.AllocHGlobal(SocketAddressBufferSize);
            try
            {
                uint addressLength = SocketAddressBufferSize;
                var convert = LibztNative.IpStringToSocketAddress(
                    remoteEndPoint.Address.ToString(),
                    checked((ushort)remoteEndPoint.Port),
                    address,
                    ref addressLength);
                if (convert != LibztNative.Ok)
                    throw new LibztException("zts_util_ipstr_to_saddr", convert, LibztNative.GetErrno());

                if (addressLength > ushort.MaxValue)
                    throw new InvalidOperationException($"libzt sockaddr length is invalid: {addressLength}");

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fd = GetFd();
                    await LibztSocketPoller.WaitAsync(
                        fd,
                        LibztNative.PollOut,
                        cancellationToken).ConfigureAwait(false);

                    var sent = LibztNative.SendTo(
                        fd,
                        contentHandle.AddrOfPinnedObject(),
                        checked((uint)bytes.Length),
                        0,
                        address,
                        checked((ushort)addressLength));
                    if (sent >= 0)
                    {
                        if (sent != bytes.Length)
                            throw new IOException($"libzt UDP partial send: requested={bytes.Length}, sent={sent}");
                        return;
                    }

                    var errno = LibztNative.GetErrno();
                    if (!LibztSocketPoller.IsWouldBlock(errno))
                        throw new LibztException("zts_bsd_sendto", sent, errno);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(address);
                contentHandle.Free();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<LibztUdpDatagram> ReceiveFromAsync(CancellationToken cancellationToken)
    {
        var buffer = Marshal.AllocHGlobal(MaxDatagramSize);
        var address = Marshal.AllocHGlobal(SocketAddressBufferSize);
        var addressLengthPointer = Marshal.AllocHGlobal(sizeof(uint));
        var text = Marshal.AllocHGlobal(LibztNative.IpStringLength);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fd = GetFd();
                await LibztSocketPoller.WaitAsync(
                    fd,
                    LibztNative.PollIn,
                    cancellationToken).ConfigureAwait(false);

                Marshal.WriteInt32(
                    addressLengthPointer,
                    SocketAddressBufferSize);
                var received = LibztNative.ReceiveFrom(
                    fd,
                    buffer,
                    checked((uint)MaxDatagramSize),
                    0,
                    address,
                    addressLengthPointer);

                if (received < 0)
                {
                    var errno = LibztNative.GetErrno();
                    if (LibztSocketPoller.IsWouldBlock(errno))
                        continue;

                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    throw new LibztException("zts_bsd_recvfrom", received, errno);
                }

                var addressLength = checked(
                    (uint)Marshal.ReadInt32(addressLengthPointer));
                ushort port = 0;
                var convert = LibztNative.SocketAddressToString(
                    address,
                    addressLength,
                    text,
                    LibztNative.IpStringLength,
                    ref port);
                if (convert != LibztNative.Ok)
                    throw new LibztException("zts_util_ntop", convert, LibztNative.GetErrno());

                var ipText = Marshal.PtrToStringAnsi(text)
                    ?? throw new IOException("libzt returned an empty UDP source address.");
                var content = new byte[received];
                Marshal.Copy(buffer, content, 0, received);
                return new LibztUdpDatagram(
                    new IPEndPoint(IPAddress.Parse(ipText), port),
                    content);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            Marshal.FreeHGlobal(addressLengthPointer);
            Marshal.FreeHGlobal(address);
            Marshal.FreeHGlobal(buffer);
        }
    }

    public ValueTask DisposeAsync()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
            _ = LibztNative.Close(fd);
        // Reset can close this socket while an in-flight SendToAsync is
        // unwinding. Its finally block still calls Release(), so disposing the
        // semaphore here creates a release-after-dispose race. It owns no
        // native socket resource and can be left for GC.
        return ValueTask.CompletedTask;
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztUdpSocket));
    }
}
