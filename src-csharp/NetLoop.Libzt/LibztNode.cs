using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NetLoop.Core;

namespace NetLoop.Libzt;

public sealed class LibztNode : IAsyncDisposable
{
    private static readonly TimeSpan NetworkRevalidationInterval = TimeSpan.FromSeconds(30);
    private readonly ulong _networkId;
    private readonly string _stateDirectory;
    private readonly TimeSpan _startupTimeout;
    private readonly LibztNative.EventCallback _eventCallback;
    private readonly TaskCompletionSource _nodeUp = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateSync = new();
    private readonly CancellationTokenSource _networkConfigStop = new();
    private readonly Channel<short> _networkConfigEvents = Channel.CreateBounded<short>(
        new BoundedChannelOptions(1) {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private Task? _networkConfigWorker;
    private LibztNetworkState? _state;
    private long _stateGeneration;
    private int _configAvailable;
    private int _joinRequested;
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

    public LibztNetworkState? State => Volatile.Read(ref _state);
    public bool NetworkConfigurationAvailable => Volatile.Read(ref _configAvailable) != 0;

    /// <summary>
    /// Raised on the background config worker, never from the native event
    /// callback. Listeners should atomically swap routing snapshots, not reset
    /// the Node or abort existing connections.
    /// </summary>
    public event Action<LibztNetworkState>? NetworkStateChanged;

    /// <summary>
    /// The ZeroTier core cannot currently supply a complete configuration.
    /// Keep the last verified state for diagnostics, but callers must fail
    /// closed for new routing decisions until the next successful query.
    /// </summary>
    public event Action<string>? NetworkConfigurationUnavailable;

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
        _networkConfigWorker = Task.Run(
            () => ProcessNetworkConfigEventsAsync(_networkConfigStop.Token));
        try
        {
            ThrowIfError("zts_node_start", LibztNative.NodeStart());
            Volatile.Write(ref _started, true);
            return await JoinAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopNetworkConfigWorkerAsync().ConfigureAwait(false);
            TryStop();
            throw;
        }
    }

    public LibztNetworkState RefreshNetworkState()
    {
        if (!_started)
            throw new InvalidOperationException("libzt node has not been started.");

        lock (_stateSync)
        {
            try
            {
                var state = QueryNetworkState();
                Volatile.Write(ref _state, state);
                Volatile.Write(ref _configAvailable, 1);
                return state;
            }
            catch
            {
                Volatile.Write(ref _configAvailable, 0);
                throw;
            }
        }
    }

    public void NotifyPhysicalNetworkChanged()
    {
        if (!Volatile.Read(ref _started))
            throw new InvalidOperationException("libzt node has not been started.");

        ThrowIfError(
            "zts_node_network_changed",
            LibztNative.NodeNetworkChanged());
        LogPhysicalNetworkRefreshRequested();
    }

    public bool TryNotifyPhysicalNetworkChanged()
    {
        // A NodeStart return does not imply NetJoin has been submitted.
        // Keep the owner's pending refresh until native network join begins,
        // otherwise an early successful refresh can be consumed too soon.
        if (!Volatile.Read(ref _started)
            || Volatile.Read(ref _joinRequested) == 0)
            return false;

        var result = LibztNative.NodeNetworkChanged();
        if (result != LibztNative.Ok)
        {
            JsonLog.Error("libzt_physical_network_refresh_failed", new {
                network = _networkId.ToString("x16"),
                api_rc = result,
                socket_error = LibztNative.GetErrno()
            });
            return false;
        }

        LogPhysicalNetworkRefreshRequested();
        return true;
    }

    private void LogPhysicalNetworkRefreshRequested()
    {
        JsonLog.Info("libzt_physical_network_refresh_requested", new {
            network = _networkId.ToString("x16"),
            node = State?.NodeId.ToString("x10")
        });
    }

    private async Task<LibztNetworkState> JoinAndWaitAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_startupTimeout);

        // The startup timeout guards native Node initialization ONLY.
        // Once the Node is up, an unavailable Controller/transport must not
        // make an otherwise healthy, non-daemon process exit permanently.
        await _nodeUp.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        var nodeId = LibztNative.NodeGetId();
        JsonLog.Info("libzt_network_join_start", new {
            network = _networkId.ToString("x16"),
            node = nodeId.ToString("x10"),
            node_online = LibztNative.NodeIsOnline() == 1
        });
        ThrowIfError("zts_net_join", LibztNative.NetJoin(_networkId));
        Volatile.Write(ref _joinRequested, 1);

        while (LibztNative.NetTransportIsReady(_networkId) != 1)
        {
            var status = LibztNative.NetGetStatus(_networkId);
            if (IsTerminalNetworkStatus(status))
                throw new LibztException($"network join status={status}", status);

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        LibztNetworkState state;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                state = RefreshNetworkState();
                if (state.ManagedAddresses.Count != 0)
                    break;
            }
            catch (LibztException ex)
                when (ex.ApiResult == LibztNative.ErrNoResult
                      && !cancellationToken.IsCancellationRequested)
            {
                // The transport can report readiness just before the native
                // network configuration appears in core. Do not treat this
                // transient state as a permanent startup failure.
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        JsonLog.Info("libzt_network_ready", new {
            network = _networkId.ToString("x16"),
            node = state.NodeId.ToString("x10"),
            node_online = LibztNative.NodeIsOnline() == 1,
            addresses = state.ManagedAddresses.Select(static x => x.ToString()).ToArray(),
            assigned_prefixes = state.AssignedPrefixes.Select(static x => x.ToString()).ToArray(),
            managed_routes = state.ManagedRoutes.Select(
                static x => new {
                    target = x.Target.ToString(),
                    via = x.Via?.ToString(),
                    flags = x.Flags,
                    metric = x.Metric
                }).ToArray(),
            generation = state.Generation
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
            return new LibztNetworkState(
                _networkId,
                LibztNative.NodeGetId(),
                addresses.Select(static x => x.Address).ToArray(),
                addresses.Select(static x => x.Prefix).Distinct().ToArray(),
                routes,
                ++_stateGeneration);
        }
        finally
        {
            var release = LibztNative.CoreLockRelease();
            if (release != LibztNative.Ok)
                JsonLog.Error("libzt_core_lock_release_failed", new { api_rc = release });
        }
    }

    private IReadOnlyList<(IPAddress Address, OverlayPrefix Prefix)> QueryAddresses()
    {
        var count = LibztNative.CoreQueryAddressCount(_networkId);
        if (count == LibztNative.ErrNoResult)
            throw new LibztException(
                "zts_core_query_addr_count(network unavailable)", count);
        if (count < 0)
            throw new LibztException("zts_core_query_addr_count", count);

        var result = new List<(IPAddress, OverlayPrefix)>(count);
        var buffer = Marshal.AllocHGlobal(LibztNative.CidrStringLength);
        try
        {
            for (var index = 0; index < count; index++)
            {
                ZeroBuffer(buffer, LibztNative.CidrStringLength);
                ThrowIfError(
                    "zts_netloop_query_addr_cidr",
                    LibztNative.CoreQueryAddressCidr(
                        _networkId, (uint)index, buffer, LibztNative.CidrStringLength));

                var value = Marshal.PtrToStringAnsi(buffer)
                    ?? throw new InvalidOperationException("libzt returned no assigned address");
                var separator = value.LastIndexOf('/');
                if (separator <= 0
                    || !IPAddress.TryParse(value[..separator], out var address))
                    throw new FormatException($"Invalid ZeroTier assigned CIDR address: {value}");

                result.Add((address, OverlayPrefix.Parse(value)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private IReadOnlyList<ManagedOverlayRoute> QueryRoutes()
    {
        var count = LibztNative.CoreQueryRouteCount(_networkId);
        if (count == LibztNative.ErrNoResult)
            throw new LibztException(
                "zts_core_query_route_count(network unavailable)", count);
        if (count < 0)
            throw new LibztException("zts_core_query_route_count", count);

        var routes = new List<ManagedOverlayRoute>(count);
        var targetBuffer = Marshal.AllocHGlobal(LibztNative.CidrStringLength);
        var viaBuffer = Marshal.AllocHGlobal(LibztNative.CidrStringLength);
        try
        {
            for (var index = 0; index < count; index++)
            {
                ZeroBuffer(targetBuffer, LibztNative.CidrStringLength);
                ZeroBuffer(viaBuffer, LibztNative.CidrStringLength);
                ThrowIfError(
                    "zts_netloop_query_route_cidr",
                    LibztNative.CoreQueryRouteCidr(
                        _networkId, (uint)index,
                        targetBuffer, viaBuffer, LibztNative.CidrStringLength,
                        out var flags, out var metric));

                var target = Marshal.PtrToStringAnsi(targetBuffer)
                    ?? throw new InvalidOperationException("libzt returned no route target");
                var viaText = Marshal.PtrToStringAnsi(viaBuffer);
                IPAddress? via = null;
                if (!string.IsNullOrWhiteSpace(viaText))
                {
                    via = IPAddress.Parse(viaText);
                    if (via.Equals(IPAddress.Any) || via.Equals(IPAddress.IPv6Any))
                        via = null;
                }

                routes.Add(new ManagedOverlayRoute(
                    OverlayPrefix.Parse(target),
                    via,
                    flags,
                    metric));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetBuffer);
            Marshal.FreeHGlobal(viaBuffer);
        }

        return routes;
    }

    private async Task ProcessNetworkConfigEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                short? eventCode = null;
                using (var heartbeat = CancellationTokenSource
                           .CreateLinkedTokenSource(cancellationToken))
                {
                    heartbeat.CancelAfter(NetworkRevalidationInterval);
                    try
                    {
                        eventCode = await _networkConfigEvents.Reader
                            .ReadAsync(heartbeat.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested
                              && heartbeat.IsCancellationRequested)
                    {
                        // Periodic native snapshot revalidation also detects
                        // a controller state disappearing without an event.
                    }
                }

                try
                {
                    // Native libzt callbacks must not acquire the core lock:
                    // the config is queried only from this dedicated worker.
                    while (State is null)
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                    var state = RefreshNetworkState();
                    JsonLog.Info("libzt_network_config_refreshed", new {
                        network = _networkId.ToString("x16"),
                        event_code = eventCode,
                        source = eventCode is null ? "periodic" : "native_event",
                        generation = state.Generation,
                        queried_at_utc = state.QueriedAtUtc,
                        assigned_addresses = state.ManagedAddresses.Select(
                            static x => x.ToString()).ToArray(),
                        assigned_prefixes = state.AssignedPrefixes.Select(
                            static x => x.ToString()).ToArray(),
                        routes = state.ManagedRoutes.Select(static x => new {
                            target = x.Target.ToString(),
                            via = x.Via?.ToString(),
                            flags = x.Flags,
                            metric = x.Metric
                        }).ToArray()
                    });
                    NetworkStateChanged?.Invoke(state);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    JsonLog.Error("libzt_network_config_refresh_failed", new {
                        event_code = eventCode,
                        error_type = ex.GetType().Name,
                        error = ex.Message
                    });
                    try
                    {
                        NetworkConfigurationUnavailable?.Invoke(ex.Message);
                    }
                    catch (Exception notificationError)
                    {
                        JsonLog.Error("libzt_network_config_unavailable_callback_failed", new {
                            error = notificationError.Message
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsNetworkConfigEvent(short eventCode)
        => eventCode is LibztNative.EventNetworkOk
            or LibztNative.EventNetworkReadyIp4
            or LibztNative.EventNetworkReadyIp6
            or LibztNative.EventNetworkUpdate
            or LibztNative.EventRouteAdded
            or LibztNative.EventRouteRemoved
            or LibztNative.EventAddressAddedIp4
            or LibztNative.EventAddressRemovedIp4
            or LibztNative.EventAddressAddedIp6
            or LibztNative.EventAddressRemovedIp6;

    private async Task StopNetworkConfigWorkerAsync()
    {
        _networkConfigStop.Cancel();
        _networkConfigEvents.Writer.TryComplete();
        if (_networkConfigWorker is not null)
            await _networkConfigWorker.ConfigureAwait(false);
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

    private void OnNativeEvent(nint message)
    {
        if (message == 0)
            return;

        try
        {
            var native = Marshal.PtrToStructure<LibztNative.EventMessage>(message);
            if (LibztPeerEvent.IsPeerEvent(native.EventCode))
            {
                try
                {
                    JsonLog.Info(
                        "libzt_peer_status",
                        LibztPeerEvent.Decode(
                            native.EventCode, _networkId, native.Peer, native.Length));
                }
                catch (Exception ex)
                {
                    JsonLog.Error("libzt_peer_event_decode_failed", new {
                        event_code = native.EventCode,
                        error = ex.Message
                    });
                    JsonLog.Info("libzt_event", new { event_code = native.EventCode });
                }
            }
            else
            {
                JsonLog.Info("libzt_event", new { event_code = native.EventCode });
            }
            if (native.EventCode == LibztNative.EventNodeUp)
                _nodeUp.TrySetResult();
            else if (native.EventCode == LibztNative.EventNodeFatalError)
                _nodeUp.TrySetException(
                    new LibztException(
                        "libzt node initialization fatal event",
                        LibztNative.ErrService));

            if (IsNetworkConfigEvent(native.EventCode)
                && (native.Network == 0
                    || unchecked((ulong)Marshal.ReadInt64(native.Network)) == _networkId))
                _networkConfigEvents.Writer.TryWrite(native.EventCode);
        }
        catch (Exception ex)
        {
            JsonLog.Error("libzt_event_decode_failed", new { error = ex.Message });
        }
    }

    private void TryStop()
    {
        if (!Volatile.Read(ref _started))
            return;

        try
        {
            var result = LibztNative.NodeStop();
            if (result != LibztNative.Ok)
                JsonLog.Error("libzt_node_stop_failed", new { api_rc = result });
        }
        finally
        {
            Volatile.Write(ref _joinRequested, 0);
            Volatile.Write(ref _started, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopNetworkConfigWorkerAsync().ConfigureAwait(false);
        TryStop();

        if (!_freed)
        {
            _freed = true;
            var result = LibztNative.NodeFree();
            if (result != LibztNative.Ok)
                JsonLog.Error("libzt_node_free_failed", new { api_rc = result });
        }

        _networkConfigStop.Dispose();
    }
}
