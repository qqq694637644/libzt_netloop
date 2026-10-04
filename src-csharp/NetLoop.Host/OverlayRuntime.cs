using NetLoop.Core;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class OverlayRuntimeController : IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly RouteSelector _selector;
    private readonly GenerationLibztTcpConnector _libztConnector;
    private readonly IProxyConnector _loopbackConnector;
    private readonly IProxyConnector _egressConnector;
    private readonly IProxyUdpTransportFactory _udpEgressFactory;
    private readonly OverlayGenerationManager _generations;
    private readonly TimeSpan _halfCloseTimeout;
    private readonly int _maxTcpTunnels;
    private readonly int _maxUdpAssociations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OverlayRuntime? _current;
    private int _disposed;

    internal OverlayRuntimeController(
        HostOptions options,
        RouteSelector selector,
        GenerationLibztTcpConnector libztConnector,
        IProxyConnector loopbackConnector,
        IProxyConnector egressConnector,
        IProxyUdpTransportFactory udpEgressFactory,
        OverlayGenerationManager generations,
        TimeSpan halfCloseTimeout,
        int maxTcpTunnels,
        int maxUdpAssociations)
    {
        _options = options;
        _selector = selector;
        _libztConnector = libztConnector;
        _loopbackConnector = loopbackConnector;
        _egressConnector = egressConnector;
        _udpEgressFactory = udpEgressFactory;
        _generations = generations;
        _halfCloseTimeout = halfCloseTimeout;
        _maxTcpTunnels = maxTcpTunnels;
        _maxUdpAssociations = maxUdpAssociations;
    }

    internal async ValueTask StartAsync(
        NetLoop.Libzt.LibztNetworkState state,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_current is not null)
                throw new InvalidOperationException("Overlay runtime is already started.");

            _current = Create(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask ReplaceAsync(
        NetLoop.Libzt.LibztNetworkState state,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var previous = _current;
            _current = null;
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);

            _current = Create(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _current;
            _current = null;
            if (current is not null)
                await current.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OverlayRuntime Create(NetLoop.Libzt.LibztNetworkState state)
    {
        var bindAddress = OverlayGenerationManager.SelectBindAddress(state);
        var overlayRouter = new RoutingConnector(
            _selector,
            overlayIngress: true,
            _options.OverlayPort,
            _libztConnector,
            _loopbackConnector,
            _egressConnector);

        var tcpAgent = new OverlaySocksAgent(
            bindAddress.ToString(),
            _options.OverlayPort,
            new Socks5ConnectionHandler(overlayRouter, _halfCloseTimeout),
            _generations,
            _maxTcpTunnels);
        try
        {
            var udpAgent = new OverlayUdpAgent(
                bindAddress,
                _options.OverlayUdpPort,
                _selector,
                _udpEgressFactory,
                _maxUdpAssociations,
                _options.UdpIdleTimeout);

            return new OverlayRuntime(tcpAgent, udpAgent, bindAddress);
        }
        catch
        {
            tcpAgent.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                await _current.DisposeAsync().ConfigureAwait(false);
                _current = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private sealed class OverlayRuntime(
        OverlaySocksAgent tcpAgent,
        OverlayUdpAgent udpAgent,
        System.Net.IPAddress bindAddress) : IAsyncDisposable
    {
        internal System.Net.IPAddress BindAddress { get; } = bindAddress;

        public async ValueTask DisposeAsync()
        {
            await udpAgent.DisposeAsync().ConfigureAwait(false);
            await tcpAgent.DisposeAsync().ConfigureAwait(false);
        }
    }
}
