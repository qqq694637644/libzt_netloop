using System.Net;
using System.Net.Sockets;
using NetLoop.Core;

namespace NetLoop.Socks;

public interface ISocks5UdpAssociation : IAsyncDisposable
{
    IPEndPoint RelayEndPoint { get; }

    Task Completion { get; }
}

public interface ISocks5UdpAssociationFactory
{
    ValueTask<ISocks5UdpAssociation> CreateAsync(
        IProxyConnection controlConnection,
        ProxyTarget declaredClientEndpoint,
        CancellationToken cancellationToken);
}

public sealed class Socks5UdpAssociationFactory : ISocks5UdpAssociationFactory, IAsyncDisposable
{
    private readonly IProxyUdpTransportFactory _transportFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _capacity;
    private int _disposed;

    public Socks5UdpAssociationFactory(
        IProxyUdpTransportFactory transportFactory,
        int maxAssociations,
        TimeSpan idleTimeout)
    {
        if (maxAssociations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAssociations));
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        _transportFactory = transportFactory;
        _idleTimeout = idleTimeout;
        _capacity = new SemaphoreSlim(maxAssociations, maxAssociations);
    }

    public async ValueTask<ISocks5UdpAssociation> CreateAsync(
        IProxyConnection controlConnection,
        ProxyTarget declaredClientEndpoint,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!await _capacity.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new IOException("SOCKS5 UDP association capacity is exhausted.");

        UdpClient? relay = null;
        IProxyUdpTransport? transport = null;
        try
        {
            var controlLocal = controlConnection.LocalEndPoint as IPEndPoint
                ?? throw new InvalidOperationException("UDP ASSOCIATE requires a TCP local endpoint.");
            var controlRemote = controlConnection.RemoteEndPoint as IPEndPoint
                ?? throw new InvalidOperationException("UDP ASSOCIATE requires a TCP remote endpoint.");

            relay = new UdpClient(new IPEndPoint(controlLocal.Address, 0));
            transport = await _transportFactory.CreateAsync(cancellationToken).ConfigureAwait(false);

            var expectedClient = GetExpectedClientUdpEndpoint(
                declaredClientEndpoint,
                controlRemote);

            return new Socks5UdpAssociation(
                relay,
                transport,
                controlRemote,
                expectedClient,
                _idleTimeout,
                cancellationToken,
                _capacity.Release);
        }
        catch
        {
            relay?.Dispose();
            if (transport is not null)
                await transport.DisposeAsync().ConfigureAwait(false);
            _capacity.Release();
            throw;
        }
    }

    private static IPEndPoint? GetExpectedClientUdpEndpoint(
        ProxyTarget declared,
        IPEndPoint controlRemote)
    {
        if (declared.Port == 0)
            return null;

        if (!IPAddress.TryParse(declared.Host, out var declaredAddress))
            throw new ProtocolViolationException(
                "SOCKS5 UDP ASSOCIATE client endpoint must be an IP address.");

        var address = declaredAddress.Equals(IPAddress.Any)
                      || declaredAddress.Equals(IPAddress.IPv6Any)
            ? controlRemote.Address
            : declaredAddress;

        return new IPEndPoint(address, declared.Port);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _capacity.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class Socks5UdpAssociation : ISocks5UdpAssociation
{
    private readonly UdpClient _relay;
    private readonly IProxyUdpTransport _transport;
    private readonly IPEndPoint _controlClientEndpoint;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _stop;
    private readonly Action _releaseCapacity;
    private readonly Task _clientToTransport;
    private readonly Task _transportToClient;
    private readonly Task _idleMonitor;
    private IPEndPoint? _clientUdpEndpoint;
    private long _lastActivity;
    private int _disposed;

    internal Socks5UdpAssociation(
        UdpClient relay,
        IProxyUdpTransport transport,
        IPEndPoint controlClientEndpoint,
        IPEndPoint? expectedClientUdpEndpoint,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken,
        Action releaseCapacity)
    {
        _relay = relay;
        _transport = transport;
        _controlClientEndpoint = controlClientEndpoint;
        _clientUdpEndpoint = expectedClientUdpEndpoint;
        _idleTimeout = idleTimeout;
        _releaseCapacity = releaseCapacity;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lastActivity = Environment.TickCount64;

        RelayEndPoint = (IPEndPoint)_relay.Client.LocalEndPoint!;
        _clientToTransport = RunGuardedAsync(
            ClientToTransportLoopAsync,
            "socks_udp_client_loop_failed");
        _transportToClient = RunGuardedAsync(
            TransportToClientLoopAsync,
            "socks_udp_transport_loop_failed");
        _idleMonitor = RunGuardedAsync(
            IdleMonitorAsync,
            "socks_udp_idle_monitor_failed");
        Completion = Task.WhenAll(_clientToTransport, _transportToClient, _idleMonitor);
    }

    public IPEndPoint RelayEndPoint { get; }

    public Task Completion { get; }

    private async Task ClientToTransportLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _relay.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var source = result.RemoteEndPoint;

            if (_clientUdpEndpoint is null
                && source.Address.Equals(_controlClientEndpoint.Address))
            {
                _clientUdpEndpoint = source;
                JsonLog.Info("socks_udp_source_claimed", new {
                    control_client = _controlClientEndpoint.ToString(),
                    udp_client = source.ToString()
                });
            }

            if (_clientUdpEndpoint is null || !source.Equals(_clientUdpEndpoint))
            {
                JsonLog.Info("socks_udp_source_dropped", new {
                    control_client = _controlClientEndpoint.ToString(),
                    expected = _clientUdpEndpoint?.ToString(),
                    source = source.ToString()
                });
                continue;
            }

            Touch();
            if (!Socks5UdpPacket.TryParse(result.Buffer, out var packet, out var error))
            {
                JsonLog.Info("socks_udp_packet_dropped", new {
                    source = source.ToString(),
                    error
                });
                continue;
            }

            await _transport.SendAsync(
                packet.Target,
                packet.Payload,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TransportToClientLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var client = _clientUdpEndpoint;
            if (client is null)
            {
                JsonLog.Info("socks_udp_response_dropped_before_client_claim", new {
                    source = datagram.Source.ToString()
                });
                continue;
            }

            var packet = Socks5UdpPacket.Build(datagram.Source, datagram.Payload);
            _ = await _relay.SendAsync(packet, client, cancellationToken).ConfigureAwait(false);
            Touch();
        }
    }

    private async Task IdleMonitorAsync(CancellationToken cancellationToken)
    {
        var poll = TimeSpan.FromSeconds(
            Math.Clamp(_idleTimeout.TotalSeconds / 4, 1, 5));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            var idleFor = Environment.TickCount64 - Interlocked.Read(ref _lastActivity);
            if (idleFor < _idleTimeout.TotalMilliseconds)
                continue;

            JsonLog.Info("socks_udp_association_idle_timeout", new {
                relay = RelayEndPoint.ToString(),
                idle_ms = idleFor
            });
            await _stop.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    private async Task RunGuardedAsync(
        Func<CancellationToken, Task> action,
        string failureEvent)
    {
        try
        {
            await action(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (SocketException ex) when (_stop.IsCancellationRequested)
        {
            JsonLog.Info("socks_udp_loop_cancelled", new {
                failure_event = failureEvent,
                socket_error = (int)ex.SocketErrorCode
            });
        }
        catch (Exception ex)
        {
            JsonLog.Error(failureEvent, new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private void Touch()
        => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        _relay.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _releaseCapacity();

        JsonLog.Info("socks_udp_association_closed", new {
            relay = RelayEndPoint.ToString()
        });
    }
}
