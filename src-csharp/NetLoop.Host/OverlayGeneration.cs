using System.Net;
using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

internal sealed class OverlayGeneration
{
    private readonly TaskCompletionSource<LibztNetworkState> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal OverlayGeneration(NetworkEpochSnapshot epoch)
    {
        Epoch = epoch;
    }

    internal NetworkEpochSnapshot Epoch { get; }

    internal Task<LibztNetworkState> Ready => _ready.Task;

    internal bool TrySetReady(LibztNetworkState state)
        => _ready.TrySetResult(state);

    internal bool TrySetException(Exception exception)
        => _ready.TrySetException(exception);
}

internal sealed class OverlayGenerationManager : IDisposable
{
    private readonly object _gate = new();
    private readonly NetworkEpoch _epoch = new();
    private OverlayGeneration _current;
    private int _disposed;

    internal OverlayGenerationManager(LibztNetworkState initialState)
    {
        _current = new OverlayGeneration(_epoch.Capture());
        _current.TrySetReady(initialState);
    }

    internal long CurrentEpoch
    {
        get
        {
            lock (_gate)
                return _current.Epoch.Value;
        }
    }

    internal OverlayGeneration Capture()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _current;
        }
    }

    internal OverlayGeneration AdvanceSoft(LibztNetworkState state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            var next = new OverlayGeneration(_epoch.Advance());
            next.TrySetReady(state);
            _current = next;
            return next;
        }
    }

    internal OverlayGeneration BeginHard()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            var next = new OverlayGeneration(_epoch.Advance());
            _current = next;
            return next;
        }
    }

    internal void CompleteHard(OverlayGeneration generation, LibztNetworkState state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, generation))
                throw new InvalidOperationException("Hard recovery generation is no longer current.");

            generation.TrySetReady(state);
        }
    }

    internal void FailHard(OverlayGeneration generation, Exception exception)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, generation))
                generation.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }

        _epoch.Dispose();
    }

    internal static IPAddress SelectBindAddress(LibztNetworkState state)
    {
        if (state.ManagedAddresses.Count == 0)
            throw new InvalidOperationException("ZeroTier network has no Managed IP.");

        return state.ManagedAddresses.FirstOrDefault(
                   static address =>
                       address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
               ?? state.ManagedAddresses[0];
    }
}

internal interface IOverlayRecoveryObserver
{
    void ReportOverlaySuccess(long epoch);

    void ReportOverlayFailure(long epoch, Exception exception);
}

internal sealed class GenerationLibztTcpConnector : IProxyConnector
{
    private readonly OverlayGenerationManager _generations;
    private readonly LibztTcpConnector _connector;

    internal GenerationLibztTcpConnector(
        OverlayGenerationManager generations,
        TimeSpan connectTimeout)
    {
        _generations = generations;
        _connector = new LibztTcpConnector(connectTimeout);
    }

    internal IOverlayRecoveryObserver? RecoveryObserver { get; set; }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _generations.Capture();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                generation.Epoch.CancellationToken);

            try
            {
                _ = await generation.Ready.WaitAsync(linked.Token).ConfigureAwait(false);
                var connection = await _connector.ConnectForGenerationAsync(
                    target,
                    generation.Epoch.CancellationToken,
                    linked.Token).ConfigureAwait(false);

                RecoveryObserver?.ReportOverlaySuccess(generation.Epoch.Value);
                return connection;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested
                      && generation.Epoch.CancellationToken.IsCancellationRequested)
            {
                JsonLog.Info("overlay_connect_generation_changed", new {
                    target = target.ToString(),
                    epoch = generation.Epoch.Value
                });
                continue;
            }
            catch (ObjectDisposedException)
                when (!cancellationToken.IsCancellationRequested
                      && generation.Epoch.CancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception ex)
            {
                RecoveryObserver?.ReportOverlayFailure(generation.Epoch.Value, ex);
                throw;
            }
        }
    }
}

internal sealed class GenerationLibztUdpSocket : IAsyncDisposable
{
    private readonly OverlayGenerationManager _generations;
    private readonly ushort _bindPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibztUdpSocket? _socket;
    private long _socketEpoch = -1;
    private int _disposed;

    internal GenerationLibztUdpSocket(
        OverlayGenerationManager generations,
        ushort bindPort = 0)
    {
        _generations = generations;
        _bindPort = bindPort;
    }

    internal async ValueTask SendToAsync(
        ReadOnlyMemory<byte> payload,
        IPEndPoint remoteEndPoint,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _generations.Capture();
            var socket = await GetSocketAsync(generation, cancellationToken).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                generation.Epoch.CancellationToken);
            try
            {
                await socket.SendToAsync(
                    payload,
                    remoteEndPoint,
                    linked.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception)
                when (!cancellationToken.IsCancellationRequested
                      && generation.Epoch.CancellationToken.IsCancellationRequested)
            {
                continue;
            }
        }
    }

    internal async ValueTask<LibztUdpDatagram> ReceiveFromAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _generations.Capture();
            var socket = await GetSocketAsync(generation, cancellationToken).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                generation.Epoch.CancellationToken);
            try
            {
                return await socket.ReceiveFromAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception)
                when (!cancellationToken.IsCancellationRequested
                      && generation.Epoch.CancellationToken.IsCancellationRequested)
            {
                continue;
            }
        }
    }

    private async ValueTask<LibztUdpSocket> GetSocketAsync(
        OverlayGeneration generation,
        CancellationToken cancellationToken)
    {
        var readyState = await generation.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (generation.Epoch.CancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(generation.Epoch.CancellationToken);

        if (_socket is { } fastPath && _socketEpoch == generation.Epoch.Value)
            return fastPath;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_socket is { } current && _socketEpoch == generation.Epoch.Value)
                return current;

            var previous = _socket;
            _socket = null;
            _socketEpoch = -1;
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);

            if (generation.Epoch.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(generation.Epoch.CancellationToken);

            var bindAddress = OverlayGenerationManager.SelectBindAddress(readyState);
            var created = LibztUdpSocket.Bind(bindAddress, _bindPort);
            created.BindGeneration(generation.Epoch.CancellationToken);
            _socket = created;
            _socketEpoch = generation.Epoch.Value;

            JsonLog.Info("libzt_udp_generation_ready", new {
                epoch = generation.Epoch.Value,
                bind = bindAddress.ToString(),
                port = _bindPort
            });
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket is not null)
            {
                await _socket.DisposeAsync().ConfigureAwait(false);
                _socket = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
