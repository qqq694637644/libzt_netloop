using System.Runtime.InteropServices;

namespace NetLoop.Libzt;

internal static class LibztNative
{
    internal const string LibraryName = "libzt";

    internal const int Ok = 0;
    internal const int ErrSocket = -1;
    internal const int ErrService = -2;
    internal const int ErrArg = -3;
    internal const int ErrNoResult = -4;
    internal const int ErrGeneral = -5;

    internal const int AfInet = 2;
    internal const int AfInet6 = 10;
    internal const int SockStream = 1;
    internal const int SockDatagram = 2;
    internal const int IpProtoTcp = 6;
    internal const int ShutWrite = 1;
    internal const int ShutReadWrite = 2;

    internal const int TcpKeepIdle = 3;
    internal const int TcpKeepInterval = 4;
    internal const int TcpKeepCount = 5;

    internal const int NetworkStatusRequestingConfiguration = 0;
    internal const int NetworkStatusOk = 1;
    internal const int NetworkStatusAccessDenied = 2;
    internal const int NetworkStatusNotFound = 3;
    internal const int NetworkStatusPortError = 4;
    internal const int NetworkStatusClientTooOld = 5;
    internal const short EventNodeUp = 200;
    internal const short EventNodeFatalError = 204;
    internal const int EAgain = 11;
    internal const int ETimedOut = 110;
    // The pinned Windows libzt/lwIP build uses UCRT errno values for
    // SO_RCVTIMEO. Keep these explicit until the native ABI is normalized.
    internal const int WindowsETimedOut = 138;
    internal const int WindowsEWouldBlock = 140;
    internal const short PollIn = 0x001;
    internal const short PollOut = 0x002;
    internal const short PollError = 0x004;
    internal const short PollInvalid = 0x008;

    internal const int IpStringLength = 46;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EventCallback(nint message);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, EntryPoint = "CSharp_zts_init_from_storage")]
    internal static extern int InitFromStorage(string path);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_init_allow_peer_cache")]
    internal static extern int InitAllowPeerCache(int allowed);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_init_set_event_handler")]
    internal static extern int InitSetEventHandler(EventCallback callback);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_node_start")]
    internal static extern int NodeStart();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_node_stop")]
    internal static extern int NodeStop();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_node_free")]
    internal static extern int NodeFree();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_node_is_online")]
    internal static extern int NodeIsOnline();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_node_get_id")]
    internal static extern ulong NodeGetId();

    [DllImport(
        LibraryName,
        EntryPoint = "zts_node_network_changed",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int NodeNetworkChanged();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_net_join")]
    internal static extern int NetJoin(ulong networkId);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_net_get_status")]
    internal static extern int NetGetStatus(ulong networkId);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_net_transport_is_ready")]
    internal static extern int NetTransportIsReady(ulong networkId);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_core_lock_obtain")]
    internal static extern int CoreLockObtain();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_core_lock_release")]
    internal static extern int CoreLockRelease();

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_core_query_addr_count")]
    internal static extern int CoreQueryAddressCount(ulong networkId);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_core_query_addr")]
    internal static extern int CoreQueryAddress(ulong networkId, int index, nint destination, int length);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_socket")]
    internal static extern int Socket(int family, int type, int protocol);

    [DllImport(
        LibraryName,
        CharSet = CharSet.Ansi,
        EntryPoint = "zts_connect",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ConnectEasy(int fd, string remoteAddress, ushort remotePort, int timeoutMs);

    [DllImport(
        LibraryName,
        CharSet = CharSet.Ansi,
        EntryPoint = "zts_bind",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BindEasy(int fd, string localAddress, ushort localPort);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_listen")]
    internal static extern int Listen(int fd, int backlog);

    [DllImport(
        LibraryName,
        CharSet = CharSet.Ansi,
        EntryPoint = "zts_accept",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AcceptEasy(int fd, nint remoteAddress, int length, ref ushort remotePort);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_read")]
    internal static extern int Read(int fd, nint buffer, uint length);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_write")]
    internal static extern int Write(int fd, nint buffer, uint length);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_sendto")]
    internal static extern int SendTo(
        int fd,
        nint buffer,
        uint length,
        int flags,
        nint address,
        ushort addressLength);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_recvfrom")]
    internal static extern int ReceiveFrom(
        int fd,
        nint buffer,
        uint length,
        int flags,
        nint address,
        nint addressLength);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_shutdown")]
    internal static extern int Shutdown(int fd, int how);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_close")]
    internal static extern int Close(int fd);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_set_no_delay")]
    internal static extern int SetNoDelay(int fd, int enabled);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_set_keepalive")]
    internal static extern int SetKeepAlive(int fd, int enabled);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_set_recv_timeout")]
    internal static extern int SetReceiveTimeout(int fd, int seconds, int microseconds);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_set_blocking")]
    internal static extern int SetBlocking(int fd, int enabled);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_poll")]
    internal static extern int Poll(
        ref PollFd fds,
        uint count,
        int timeoutMilliseconds);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_setsockopt")]
    internal static extern int SetSocketOption(int fd, int level, int option, nint value, ushort valueLength);

    [DllImport(
        LibraryName,
        EntryPoint = "zts_get_last_socket_error",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetLastSocketError(int fd);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_errno_get")]
    internal static extern int GetErrno();

    [DllImport(
        LibraryName,
        CharSet = CharSet.Ansi,
        EntryPoint = "zts_util_ipstr_to_saddr",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int IpStringToSocketAddress(
        string address,
        ushort port,
        nint socketAddress,
        ref uint addressLength);

    [DllImport(
        LibraryName,
        CharSet = CharSet.Ansi,
        EntryPoint = "zts_util_ntop",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SocketAddressToString(
        nint socketAddress,
        uint addressLength,
        nint destination,
        int destinationLength,
        ref ushort port);

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventMessage
    {
        internal short EventCode;
        internal nint Node;
        internal nint Network;
        internal nint NetIf;
        internal nint Route;
        internal nint Peer;
        internal nint Address;
        internal nint Cache;
        internal int Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        internal int Fd;
        internal short Events;
        internal short Revents;
    }
}
