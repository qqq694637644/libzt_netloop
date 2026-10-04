using System.Net;
using System.Runtime.InteropServices;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztNode : IAsyncDisposable
{
    private readonly ulong _networkId;
    private readonly string _stateDirectory;
    private readonly TimeSpan _startupTimeout;
    private readonly LibztNative.EventCallback _eventCallback;
    private bool _started;
    private bool _freed;

    public LibztNode(ulong networkId, string stateDirectory, TimeSpan startupTimeout)
    {
        _networkId = networkId;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _startupTimeout = startupTimeout;
        _eventCallback = OnNativeEvent;
    }

    public ulong NetworkId => _networkId;

    public LibztNetworkState? State { get; private set; }

    public async Task<LibztNetworkState> StartAsync(CancellationToken cancellationToken)
    {
        if (_freed)
            throw new ObjectDisposedException(nameof(LibztNode));
        if (_started)
            return State ?? throw new InvalidOperationException("libzt node is started without network state.");

        Directory.CreateDirectory(_stateDirectory);
        var peerCache = Path.Combine(_stateDirectory, "peers.d");
        if (Directory.Exists(peerCache))
        {
            Directory.Delete(peerCache, recursive: true);
            JsonLog.Info("libzt_peer_cache_cleared", new { path = peerCache });
        }

        ThrowIfError("zts_init_from_storage", LibztNative.InitFromStorage(_stateDirectory));
        ThrowIfError("zts_init_allow_peer_cache", LibztNative.InitAllowPeerCache(0));
        ThrowIfError("zts_init_set_event_handler", LibztNative.InitSetEventHandler(_eventCallback));
        ThrowIfError("zts_node_start", LibztNative.NodeStart());
        _started = true;

        try
        {
            State = await JoinAndWaitAsync(cancellationToken).ConfigureAwait(false);
            return State;
        }
        catch
        {
            TryStop();
            throw;
        }
    }

    public async Task<LibztNetworkState> RestartAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            throw new InvalidOperationException("libzt node has not been started.");

        JsonLog.Info("libzt_hard_recovery_start", new { network = _networkId.ToString("x16") });
        ThrowIfError("zts_node_stop", LibztNative.NodeStop());
        _started = false;

        ThrowIfError("zts_node_start", LibztNative.NodeStart());
        _started = true;
        State = await JoinAndWaitAsync(cancellationToken).ConfigureAwait(false);
        JsonLog.Info("libzt_hard_recovery_ready", new {
            network = _networkId.ToString("x16"),
            node = State.NodeId.ToString("x10"),
            addresses = State.ManagedAddresses.Select(static x => x.ToString()).ToArray()
        });
        return State;
    }

    public LibztNetworkState RefreshNetworkState()
    {
        if (!_started)
            throw new InvalidOperationException("libzt node has not been started.");

        State = QueryNetworkState();
        return State;
    }

    private async Task<LibztNetworkState> JoinAndWaitAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_startupTimeout);
        var token = timeoutCts.Token;

        JsonLog.Info("libzt_wait_node_online", new { network = _networkId.ToString("x16") });
        while (LibztNative.NodeIsOnline() != 1)
            await Task.Delay(100, token).ConfigureAwait(false);

        var nodeId = LibztNative.NodeGetId();
        JsonLog.Info("libzt_node_online", new { node = nodeId.ToString("x10") });

        ThrowIfError("zts_net_join", LibztNative.NetJoin(_networkId));

        while (LibztNative.NetTransportIsReady(_networkId) != 1)
        {
            var status = LibztNative.NetGetStatus(_networkId);
            if (IsTerminalNetworkStatus(status))
                throw new LibztException($"network join status={status}", status);

            await Task.Delay(250, token).ConfigureAwait(false);
        }

        LibztNetworkState state;
        while (true)
        {
            state = QueryNetworkState();
            if (state.ManagedAddresses.Count != 0)
                break;

            await Task.Delay(100, token).ConfigureAwait(false);
        }

        JsonLog.Info("libzt_network_ready", new {
            network = _networkId.ToString("x16"),
            node = state.NodeId.ToString("x10"),
            addresses = state.ManagedAddresses.Select(static x => x.ToString()).ToArray(),
            routes = state.Routes
        });
        return state;
    }

    private LibztNetworkState QueryNetworkState()
    {
        ThrowIfError("zts_core_lock_obtain", LibztNative.CoreLockObtain());
        try
        {
            var addresses = QueryAddresses();
            var routes = QueryRoutes();
            return new LibztNetworkState(_networkId, LibztNative.NodeGetId(), addresses, routes);
        }
        finally
        {
            var release = LibztNative.CoreLockRelease();
            if (release != LibztNative.Ok)
                JsonLog.Error("libzt_core_lock_release_failed", new { api_rc = release });
        }
    }

    private IReadOnlyList<IPAddress> QueryAddresses()
    {
        var count = LibztNative.CoreQueryAddressCount(_networkId);
        if (count < 0)
            throw new LibztException("zts_core_query_addr_count", count);

        var result = new List<IPAddress>(count);
        var buffer = Marshal.AllocHGlobal(LibztNative.IpStringLength);
        try
        {
            for (var index = 0; index < count; index++)
            {
                ZeroBuffer(buffer, LibztNative.IpStringLength);
                ThrowIfError(
                    "zts_core_query_addr",
                    LibztNative.CoreQueryAddress(_networkId, index, buffer, LibztNative.IpStringLength));

                var value = Marshal.PtrToStringAnsi(buffer);
                if (!string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out var address))
                    result.Add(address);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private IReadOnlyList<LibztRouteInfo> QueryRoutes()
    {
        var count = LibztNative.CoreQueryRouteCount(_networkId);
        if (count < 0)
            throw new LibztException("zts_core_query_route_count", count);

        var result = new List<LibztRouteInfo>(count);
        var target = Marshal.AllocHGlobal(LibztNative.IpStringLength);
        var via = Marshal.AllocHGlobal(LibztNative.IpStringLength);
        try
        {
            for (var index = 0; index < count; index++)
            {
                ZeroBuffer(target, LibztNative.IpStringLength);
                ZeroBuffer(via, LibztNative.IpStringLength);
                ushort flags = 0;
                ushort metric = 0;
                ThrowIfError(
                    "zts_core_query_route",
                    LibztNative.CoreQueryRoute(
                        _networkId,
                        index,
                        target,
                        via,
                        LibztNative.IpStringLength,
                        ref flags,
                        ref metric));

                result.Add(new LibztRouteInfo(
                    Marshal.PtrToStringAnsi(target) ?? string.Empty,
                    Marshal.PtrToStringAnsi(via) ?? string.Empty,
                    flags,
                    metric));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(via);
        }

        return result;
    }

    private static unsafe void ZeroBuffer(nint buffer, int length)
        => new Span<byte>((void*)buffer, length).Clear();

    private static bool IsTerminalNetworkStatus(int status)
        => status is LibztNative.NetworkStatusAccessDenied
            or LibztNative.NetworkStatusNotFound
            or LibztNative.NetworkStatusPortError
            or LibztNative.NetworkStatusClientTooOld;

    private static void ThrowIfError(string operation, int result)
    {
        if (result != LibztNative.Ok)
            throw new LibztException(operation, result, LibztNative.GetErrno());
    }

    private static void OnNativeEvent(nint message)
    {
        if (message == 0)
            return;

        try
        {
            var native = Marshal.PtrToStructure<LibztNative.EventMessage>(message);
            JsonLog.Info("libzt_event", new { event_code = native.EventCode });
        }
        catch (Exception ex)
        {
            JsonLog.Error("libzt_event_decode_failed", new { error = ex.Message });
        }
    }

    private void TryStop()
    {
        if (!_started)
            return;

        try
        {
            var result = LibztNative.NodeStop();
            if (result != LibztNative.Ok)
                JsonLog.Error("libzt_node_stop_failed", new { api_rc = result });
        }
        finally
        {
            _started = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        TryStop();

        if (!_freed)
        {
            _freed = true;
            var result = LibztNative.NodeFree();
            if (result != LibztNative.Ok)
                JsonLog.Error("libzt_node_free_failed", new { api_rc = result });
        }

        return ValueTask.CompletedTask;
    }
}
