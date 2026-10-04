using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed record LibztUdpDatagram(IPEndPoint RemoteEndPoint, byte[] Payload);

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

            var timeout = LibztNative.SetReceiveTimeout(fd, 1, 0);
            if (timeout != LibztNative.Ok)
                throw new LibztException(
                    "zts_set_recv_timeout(udp)",
                    timeout,
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
        ReadOnlyMemory<byte> payload,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaxDatagramSize)
            throw new ArgumentOutOfRangeException(nameof(payload), "UDP datagram exceeds 65535 bytes.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => {
                cancellationToken.ThrowIfCancellationRequested();
                var fd = GetFd();
                var bytes = payload.ToArray();
                var payloadHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
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

                    var sent = LibztNative.SendTo(
                        fd,
                        payloadHandle.AddrOfPinnedObject(),
                        checked((uint)bytes.Length),
                        0,
                        address,
                        checked((ushort)addressLength));
                    if (sent < 0)
                        throw new LibztException("zts_bsd_sendto", sent, LibztNative.GetErrno());
                    if (sent != bytes.Length)
                        throw new IOException($"libzt UDP partial send: requested={bytes.Length}, sent={sent}");
                }
                finally
                {
                    Marshal.FreeHGlobal(address);
                    payloadHandle.Free();
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<LibztUdpDatagram> ReceiveFromAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(
                () => ReceiveOne(cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            if (result is not null)
                return result;
        }
    }

    private LibztUdpDatagram? ReceiveOne(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fd = GetFd();
        var bytes = new byte[MaxDatagramSize];
        var payloadHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var address = Marshal.AllocHGlobal(SocketAddressBufferSize);
        var addressLengthPointer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(addressLengthPointer, SocketAddressBufferSize);
            var received = LibztNative.ReceiveFrom(
                fd,
                payloadHandle.AddrOfPinnedObject(),
                checked((uint)bytes.Length),
                0,
                address,
                addressLengthPointer);

            if (received < 0)
            {
                var errno = LibztNative.GetErrno();
                if (IsTransientReceiveError(errno))
                    return null;

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new LibztException("zts_bsd_recvfrom", received, errno);
            }

            var addressLength = checked((uint)Marshal.ReadInt32(addressLengthPointer));
            var text = Marshal.AllocHGlobal(LibztNative.IpStringLength);
            try
            {
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
                var payload = bytes.AsSpan(0, received).ToArray();
                return new LibztUdpDatagram(
                    new IPEndPoint(IPAddress.Parse(ipText), port),
                    payload);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(addressLengthPointer);
            Marshal.FreeHGlobal(address);
            payloadHandle.Free();
        }
    }

    private static bool IsTransientReceiveError(int errno)
        => errno is LibztNative.EAgain
            or LibztNative.ETimedOut
            or LibztNative.WindowsETimedOut
            or LibztNative.WindowsEWouldBlock;

    public ValueTask DisposeAsync()
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
            _ = LibztNative.Close(fd);
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private int GetFd()
    {
        var fd = Volatile.Read(ref _fd);
        return fd >= 0 ? fd : throw new ObjectDisposedException(nameof(LibztUdpSocket));
    }
}
