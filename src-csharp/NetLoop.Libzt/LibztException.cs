namespace NetLoop.Libzt;

public sealed class LibztException : IOException
{
    public LibztException(string operation, int apiResult, int socketError = 0)
        : base($"{operation} failed: api_rc={apiResult}, socket_error={socketError}")
    {
        Operation = operation;
        ApiResult = apiResult;
        SocketError = socketError;
    }

    public string Operation { get; }

    public int ApiResult { get; }

    public int SocketError { get; }

    public bool IsNetworkUnavailable => ApiResult == LibztNative.ErrNoResult;

    /// <summary>
    /// Only explicit network/address-not-ready errors during initial Agent
    /// socket setup may wait for Controller/libzt recovery. EADDRINUSE,
    /// unsupported options and unknown native errors remain terminal.
    /// Never treat an established connection's I/O errors as a bind retry.
    /// </summary>
    public bool IsTransientRuntimeBindFailure
        => ApiResult == LibztNative.ErrSocket
           && SocketError is (
               LibztNative.EAddressNotAvailable
               or LibztNative.ENetworkDown
               or LibztNative.ENetworkUnreachable
               or LibztNative.EHostUnreachable)
           && Operation is (
               "zts_bsd_socket(listener)"
               or "zts_bsd_bind_easy"
               or "zts_bsd_listen"
               or "zts_set_blocking(listener)"
               or "zts_bsd_socket(udp)"
               or "zts_bind(udp)"
               or "zts_set_blocking(udp)");
}
