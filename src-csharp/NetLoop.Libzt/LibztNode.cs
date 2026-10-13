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
    private readonly bool _enableNativeTrace;
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
    private Task? _underlayTraceWorker;
    private LibztNetworkState? _state;
    private long _stateGeneration;
    private int _configAvailable;
    private int _joinRequested;
    private long _failedPhysicalRefreshAttempts;
    private bool _started;
    private bool _freed;

    public LibztNode(
        ulong networkId, string stateDirectory, TimeSpan startupTimeout,
        bool enableNativeTrace = false)
    {
        _networkId = networkId;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _startupTimeout = startupTimeout;
        _enableNativeTrace = enableNativeTrace;
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
        // Install the Core trace callback only for explicit information-level
        // logging; normal/none mode never samples or queues Native packets.
        ThrowIfError("zts_netloop_set_underlay_trace_enabled",
            LibztNative.SetUnderlayTraceEnabled(_enableNativeTrace ? 1 : 0));
        _networkConfigWorker = Task.Run(
            () => ProcessNetworkConfigEventsAsync(_networkConfigStop.Token));
        try
        {
            ThrowIfError("zts_node_start", LibztNative.NodeStart());
            Volatile.Write(ref _started, true);
            if (_enableNativeTrace)
                _underlayTraceWorker = Task.Run(
                    () => ProcessUnderlayTraceAsync(_networkConfigStop.Token));
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

    public bool TryNotifyPhysicalNetworkChanged(ulong handoverEpoch)
    {
        // A NodeStart return does not imply NetJoin has been submitted.
        // Keep the owner's pending refresh until native network join begins,
        // otherwise an early successful refresh can be consumed too soon.
        if (!Volatile.Read(ref _started)
            || Volatile.Read(ref _joinRequested) == 0)
            return false;

        var result = LibztNative.NodeNetworkChanged(handoverEpoch);
        if (result != LibztNative.Ok)
        {
            // A down native service might reject a refresh repeatedly. Keep
            // the full exact counter without filling JSONL with 2 logs/sec.
            var errors = Interlocked.Increment(ref _failedPhysicalRefreshAttempts);
            if ((errors & (errors - 1)) == 0)
                JsonLog.Error("libzt_physical_network_refresh_failed", new {
                    network = _networkId.ToString("x16"),
                    api_rc = result,
                    socket_error = LibztNative.GetErrno(),
                    failures_since_success = errors
                });
            return false;
        }

        Interlocked.Exchange(ref _failedPhysicalRefreshAttempts, 0);
        LogPhysicalNetworkRefreshRequested(handoverEpoch);
        return true;
    }

    private void LogPhysicalNetworkRefreshRequested(ulong handoverEpoch)
    {
        JsonLog.Info("libzt_physical_network_refresh_requested", new {
            network = _networkId.ToString("x16"),
            node = State?.NodeId.ToString("x10"),
            handover_epoch = handoverEpoch
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

    private async Task ProcessUnderlayTraceAsync(CancellationToken cancellationToken)
    {
        ulong previousDropped = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Bound both native memory and the amount of managed log work
                // in one polling cycle. A full queue reports dropped records.
                for (var index = 0; index < 128; ++index)
                {
                    var rc = LibztNative.PollUnderlayTrace(
                        out var sample, out var dropped);
                    if (dropped != previousDropped)
                    {
                        JsonLog.Error("libzt_native_underlay_records_dropped", new {
                            dropped_since_previous = dropped - previousDropped,
                            dropped_total = dropped
                        });
                        previousDropped = dropped;
                    }
                    if (rc == 0)
                        break;
                    if (rc < 0)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            JsonLog.Error("libzt_native_underlay_poll_failed", new {
                                api_rc = rc
                            });
                        break;
                    }

                    // Keep draining the bounded native ring without
                    // materializing expensive JSON diagnostics when disabled.
                    if (!JsonLog.IsEnabled(JsonLogLevel.Info))
                        continue;

                    var kind = LibztUnderlayDiagnostics.NativeKind(sample.Kind);
                    JsonLog.Info("libzt_native_underlay_trace", new {
                        native_timestamp_utc =
                            DateTimeOffset.FromUnixTimeMilliseconds(sample.TimestampMs),
                        kind,
                        peer_id = sample.PeerId == 0 ? null : sample.PeerId.ToString("x10"),
                        endpoint = string.IsNullOrEmpty(sample.Endpoint)
                            ? null : sample.Endpoint,
                        local_endpoint = string.IsNullOrEmpty(sample.LocalEndpoint)
                            ? null : sample.LocalEndpoint,
                        physical_interface = string.IsNullOrEmpty(sample.InterfaceName)
                            ? null : sample.InterfaceName,
                        packet_id = sample.Kind is 5 or 6 or 10 or 11 or 14 or 15
                            or 17 or 18 or 20 or 25 or 26
                            ? sample.Reference.ToString("x16") : null,
                        hello_request_packet_id = sample.Kind is 12 or 13
                            ? sample.Reference.ToString("x16") : null,
                        echo_request_packet_id = sample.Kind == 19
                            ? sample.Reference.ToString("x16") : null,
                        binder_start_native_ms = sample.Kind is 2 or 3
                            ? (long?)sample.Reference : null,
                        planet_last_receive_native_ms = sample.Kind == 8
                            ? (long?)sample.Reference : null,
                        last_global_receive_native_ms = sample.Kind == 7
                            ? (long?)sample.Reference : null,
                        last_upstream_receive_native_ms = sample.Kind == 16
                            && sample.Reference != 0 ? (long?)sample.Reference : null,
                        upstream_age_ms = sample.Kind == 16
                            ? sample.Result : (int?)null,
                        peer_path_last_receive_native_ms = sample.Kind is 23 or 28
                            ? (long?)sample.Reference : null,
                        surface_observation_native_ms = sample.Kind is 21 or 22
                            ? (long?)sample.Reference : null,
                        surface_last_seen_native_ms = sample.Kind == 29
                            ? (long?)sample.Reference : null,
                        memorized_lookup_native_ms = sample.Kind == 24
                            ? (long?)sample.Reference : null,
                        last_upstream_full_hello_native_ms = sample.Kind == 27
                            ? (long?)sample.Reference : null,
                        handover_revalidation_native_ms = sample.Kind == 30
                            ? (long?)sample.Reference : null,
                        reason_flags = sample.Flags,
                        byte_count = sample.ByteCount,
                        result = sample.Result,
                        primary_port = sample.PrimaryPort,
                        secondary_port = sample.SecondaryPort,
                        previous_secondary_port = sample.PreviousSecondaryPort,
                        // UDP header identities are unverified observations.
                        wire_header_authenticated = sample.Kind is 5 or 6
                            ? false : (bool?)null,
                        // Receive-side core protocol records appear only
                        // after ZeroTier's existing MAC/identity verification.
                        core_protocol_authenticated = sample.Kind is 11 or 13
                            or 15 or 19 or 20 or 25
                            ? true : (bool?)null
                    });
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("libzt_native_underlay_worker_failed", new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
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
        if (_underlayTraceWorker is not null)
            await _underlayTraceWorker.ConfigureAwait(false);
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
            if (JsonLog.IsEnabled(JsonLogLevel.Info)
                && LibztPeerEvent.IsPeerEvent(native.EventCode))
            {
                try
                {
                    var peer = LibztPeerEvent.Decode(
                        native.EventCode, _networkId, native.Peer, native.Length);
                    JsonLog.Info("libzt_peer_status", peer);
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
            else if (JsonLog.IsEnabled(JsonLogLevel.Info)
                     && !LibztPeerEvent.IsPeerEvent(native.EventCode))
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
