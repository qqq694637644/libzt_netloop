using System.Net;
using System.Net.Sockets;
using NetLoop.Core;

namespace NetLoop.Socks;

public sealed class Socks5UdpProxyTransportFactory : IProxyUdpTransportFactory
{
    private readonly IProxyConnector _transportConnector;
    private readonly ProxyTarget _proxyEndpoint;
    private readonly string? _username;
    private readonly string? _password;

    public Socks5UdpProxyTransportFactory(
        IProxyConnector transportConnector,
        ProxyTarget proxyEndpoint,
        string? username = null,
        string? password = null)
    {
        _transportConnector = transportConnector;
        _proxyEndpoint = proxyEndpoint;
        _username = username;
        _password = password;
    }

    public async ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken cancellationToken)
    {
        IProxyConnection? control = null;
        UdpClient? udp = null;
        try
        {
            control = await _transportConnector.ConnectAsync(
                _proxyEndpoint,
                cancellationToken).ConfigureAwait(false);

            await Socks5ClientHandshake.NegotiateAsync(
                control,
                _username,
                _password,
                cancellationToken).ConfigureAwait(false);

            var controlRemote = control.RemoteEndPoint as IPEndPoint
                ?? throw new InvalidOperationException(
                    "SOCKS5 UDP upstream requires a TCP remote endpoint.");

            var proxyAddress = NormalizeAddress(controlRemote.Address);
            var udpFamily = proxyAddress.AddressFamily;
            var wildcard = udpFamily == AddressFamily.InterNetwork
                ? IPAddress.Any
                : IPAddress.IPv6Any;
            udp = new UdpClient(udpFamily);
            udp.Client.Bind(new IPEndPoint(wildcard, 0));

            var request = Socks5Protocol.BuildTargetRequest(
                Socks5Protocol.UdpAssociate,
                new ProxyTarget(
                    wildcard.ToString(),
                    0));
            await control.WriteAsync(
                request,
                request.Length,
                cancellationToken).ConfigureAwait(false);

            var header = new byte[4];
            await Socks5Protocol.ReadExactlyAsync(
                control,
                header,
                header.Length,
                cancellationToken).ConfigureAwait(false);

            if (header[0] != Socks5Protocol.Version)
                throw new ProtocolViolationException(
                    $"Invalid SOCKS5 UDP ASSOCIATE reply version {header[0]}.");
            if (header[1] != Socks5Protocol.ReplySucceeded)
            {
                throw new IOException(
                    $"SOCKS5 UDP ASSOCIATE failed with reply 0x{header[1]:x2}.");
            }

            var relayTarget = await Socks5Protocol.ReadTargetAsync(
                control,
                header[3],
                cancellationToken,
                allowZeroPort: true).ConfigureAwait(false);
            if (relayTarget.Port == 0)
                throw new ProtocolViolationException(
                    "SOCKS5 UDP ASSOCIATE returned relay port zero.");

            var relay = await ResolveRelayAsync(
                relayTarget,
                new IPEndPoint(proxyAddress, controlRemote.Port),
                udp.Client.AddressFamily,
                cancellationToken).ConfigureAwait(false);

            return new Socks5UdpProxyTransport(
                control,
                udp,
                relay,
                cancellationToken);
        }
        catch
        {
            udp?.Dispose();
            if (control is not null)
                await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<IPEndPoint> ResolveRelayAsync(
        ProxyTarget relayTarget,
        IPEndPoint controlRemote,
        AddressFamily udpFamily,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(relayTarget.Host, out var address))
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                address = controlRemote.Address;

            if (address.AddressFamily != udpFamily)
            {
                if (address.IsIPv4MappedToIPv6 && udpFamily == AddressFamily.InterNetwork)
                    address = address.MapToIPv4();
                else if (udpFamily == AddressFamily.InterNetworkV6
                         && address.AddressFamily == AddressFamily.InterNetwork)
                    address = address.MapToIPv6();
                else
                    throw new ProtocolViolationException(
                        $"SOCKS5 UDP relay family {address.AddressFamily} does not match local UDP family {udpFamily}.");
            }

            return new IPEndPoint(address, relayTarget.Port);
        }

        var addresses = await Dns.GetHostAddressesAsync(
            relayTarget.Host,
            cancellationToken).ConfigureAwait(false);
        var resolved = addresses.FirstOrDefault(value => value.AddressFamily == udpFamily);
        if (resolved is null && udpFamily == AddressFamily.InterNetworkV6)
        {
            var ipv4 = addresses.FirstOrDefault(
                static value => value.AddressFamily == AddressFamily.InterNetwork);
            resolved = ipv4?.MapToIPv6();
        }

        return new IPEndPoint(
            resolved ?? throw new SocketException((int)SocketError.HostNotFound),
            relayTarget.Port);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

internal sealed class Socks5UdpProxyTransport : IProxyUdpTransport
{
    private readonly IProxyConnection _control;
    private readonly UdpClient _udp;
    private readonly IPEndPoint _relay;
    private readonly CancellationTokenSource _stop;
    private readonly Task _controlMonitor;
    private int _disposed;

    internal Socks5UdpProxyTransport(
        IProxyConnection control,
        UdpClient udp,
        IPEndPoint relay,
        CancellationToken cancellationToken)
    {
        _control = control;
        _udp = udp;
        _relay = relay;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _controlMonitor = MonitorControlAsync(_stop.Token);

        JsonLog.Info("upstream_socks_udp_ready", new {
            proxy = control.RemoteEndPoint?.ToString(),
            relay = relay.ToString(),
            local_udp = udp.Client.LocalEndPoint?.ToString()
        });
    }

    public async ValueTask SendAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token);

        var packet = Socks5UdpPacket.Build(target, payload.Span);
        _ = await _udp.SendAsync(
            packet,
            _relay,
            linked.Token).ConfigureAwait(false);
    }

    public async ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token);

        while (true)
        {
            var result = await _udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
            if (!EndPointEquals(result.RemoteEndPoint, _relay))
            {
                JsonLog.Info("upstream_socks_udp_source_dropped", new {
                    expected = _relay.ToString(),
                    source = result.RemoteEndPoint.ToString()
                });
                continue;
            }

            if (!Socks5UdpPacket.TryParse(result.Buffer, out var packet, out var error))
            {
                JsonLog.Info("upstream_socks_udp_packet_dropped", new {
                    relay = _relay.ToString(),
                    error
                });
                continue;
            }

            return new ProxyUdpDatagram(
                packet.Target,
                packet.Payload.ToArray());
        }
    }

    private async Task MonitorControlAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _control.ReadAsync(
                    buffer,
                    buffer.Length,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    JsonLog.Info("upstream_socks_udp_control_closed", new {
                        relay = _relay.ToString()
                    });
                    await _stop.CancelAsync().ConfigureAwait(false);
                    return;
                }

                JsonLog.Info("upstream_socks_udp_unexpected_control_data", new {
                    relay = _relay.ToString(),
                    bytes = read
                });
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("upstream_socks_udp_control_failed", new {
                relay = _relay.ToString(),
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private static bool EndPointEquals(IPEndPoint left, IPEndPoint right)
    {
        if (left.Port != right.Port)
            return false;
        if (left.Address.Equals(right.Address))
            return true;
        if (left.Address.IsIPv4MappedToIPv6)
            return left.Address.MapToIPv4().Equals(right.Address);
        if (right.Address.IsIPv4MappedToIPv6)
            return right.Address.MapToIPv4().Equals(left.Address);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        _udp.Dispose();
        await _control.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _controlMonitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(Socks5UdpProxyTransport));
        if (_stop.IsCancellationRequested)
            throw new IOException("SOCKS5 UDP association control connection is closed.");
    }
}
