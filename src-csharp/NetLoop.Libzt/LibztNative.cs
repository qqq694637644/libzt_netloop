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

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_core_query_route_count")]
    internal static extern int CoreQueryRouteCount(ulong networkId);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_core_query_route")]
    internal static extern int CoreQueryRoute(
        ulong networkId,
        int index,
        nint target,
        nint via,
        int length,
        ref ushort flags,
        ref ushort metric);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_socket")]
    internal static extern int Socket(int family, int type, int protocol);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, EntryPoint = "CSharp_zts_bsd_connect_easy")]
    internal static extern int ConnectEasy(int fd, int family, string remoteAddress, ushort remotePort, int timeoutMs);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, EntryPoint = "CSharp_zts_bsd_bind_easy")]
    internal static extern int BindEasy(int fd, int family, string localAddress, ushort localPort);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_listen")]
    internal static extern int Listen(int fd, int backlog);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, EntryPoint = "CSharp_zts_bsd_accept_easy")]
    internal static extern int AcceptEasy(int fd, nint remoteAddress, int length, ref int remotePort);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_read")]
    internal static extern int Read(int fd, nint buffer, uint length);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_write")]
    internal static extern int Write(int fd, nint buffer, uint length);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_shutdown")]
    internal static extern int Shutdown(int fd, int how);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_close")]
    internal static extern int Close(int fd);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_set_no_delay")]
    internal static extern int SetNoDelay(int fd, int enabled);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_set_keepalive")]
    internal static extern int SetKeepAlive(int fd, int enabled);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_bsd_setsockopt")]
    internal static extern int SetSocketOption(int fd, int level, int option, nint value, ushort valueLength);

    [DllImport(
        LibraryName,
        EntryPoint = "zts_get_last_socket_error",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetLastSocketError(int fd);

    [DllImport(LibraryName, EntryPoint = "CSharp_zts_errno_get")]
    internal static extern int GetErrno();

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
}
