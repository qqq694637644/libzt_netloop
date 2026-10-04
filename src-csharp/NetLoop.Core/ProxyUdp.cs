using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NetLoop.Core;

public sealed record ProxyUdpDatagram(ProxyTarget Source, byte[] Payload);

public interface IProxyUdpTransport : IAsyncDisposable
{
    ValueTask SendAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IProxyUdpTransportFactory
{
    ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken cancellationToken);
}

public sealed class DirectUdpTransportFactory : IProxyUdpTransportFactory
{
    public ValueTask<IProxyUdpTransport> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IProxyUdpTransport>(new SystemUdpTransport());
    }
}

public sealed class SystemUdpTransport : IProxyUdpTransport
{
    private const int MaxDestinations = 256;
    private static readonly TimeSpan DestinationIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly UdpClient _ipv4;
    private readonly UdpClient _ipv6;
    private readonly ConcurrentDictionary<IPEndPoint, long> _destinations = new();
    private readonly Channel<ProxyUdpDatagram> _received = Channel.CreateUnbounded<ProxyUdpDatagram>(
        new UnboundedChannelOptions {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _receiveLoops;
    private int _disposed;

    public SystemUdpTransport()
    {
        _ipv4 = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _ipv6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Any, 0));
        _receiveLoops = [
            ReceiveLoopAsync(_ipv4, _stop.Token),
            ReceiveLoopAsync(_ipv6, _stop.Token)
        ];
    }

    public async ValueTask SendAsync(
        ProxyTarget target,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var address = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        var endpoint = new IPEndPoint(address, target.Port);
        TouchDestination(endpoint);

        var client = address.AddressFamily == AddressFamily.InterNetwork ? _ipv4 : _ipv6;
        _ = await client.SendAsync(payload, endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProxyUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _received.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!_destinations.ContainsKey(result.RemoteEndPoint))
                {
                    JsonLog.Info("udp_unexpected_source_dropped", new {
                        transport = "system",
                        source = result.RemoteEndPoint.ToString()
                    });
                    continue;
                }

                _destinations[result.RemoteEndPoint] = Environment.TickCount64;
                await _received.Writer.WriteAsync(
                    new ProxyUdpDatagram(
                        new ProxyTarget(
                            result.RemoteEndPoint.Address.ToString(),
                            checked((ushort)result.RemoteEndPoint.Port)),
                        result.Buffer),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
        {
            JsonLog.Info("udp_receive_cancelled", new {
                transport = "system",
                socket_error = (int)ex.SocketErrorCode
            });
        }
        catch (Exception ex)
        {
            _received.Writer.TryComplete(ex);
            JsonLog.Error("udp_receive_failed", new {
                transport = "system",
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IPAddress> ResolveAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        if (target.TryGetIPAddress(out var address))
            return address;

        var addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        return addresses.FirstOrDefault(static value => value.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(static value => value.AddressFamily == AddressFamily.InterNetworkV6)
            ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);
    }

    private void TouchDestination(IPEndPoint endpoint)
    {
        var now = Environment.TickCount64;
        _destinations[endpoint] = now;
        if (_destinations.Count <= MaxDestinations)
            return;

        var idleBefore = now - (long)DestinationIdleTimeout.TotalMilliseconds;
        foreach (var entry in _destinations)
        {
            if (entry.Value < idleBefore)
                _destinations.TryRemove(entry.Key, out _);
        }

        if (_destinations.Count <= MaxDestinations)
            return;

        var oldest = _destinations.MinBy(static entry => entry.Value);
        _destinations.TryRemove(oldest.Key, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        _ipv4.Dispose();
        _ipv6.Dispose();

        try
        {
            await Task.WhenAll(_receiveLoops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _received.Writer.TryComplete();
        _stop.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(SystemUdpTransport));
    }
}
