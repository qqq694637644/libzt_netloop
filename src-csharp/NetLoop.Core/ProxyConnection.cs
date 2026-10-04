using System.Net.Sockets;

namespace NetLoop.Core;

public interface IProxyConnection : IAsyncDisposable
{
    string Description { get; }

    ValueTask<int> ReadAsync(byte[] buffer, int count, CancellationToken cancellationToken);

    ValueTask WriteAsync(byte[] buffer, int count, CancellationToken cancellationToken);

    ValueTask ShutdownWriteAsync(CancellationToken cancellationToken);
}

public interface IProxyConnector
{
    ValueTask<IProxyConnection> ConnectAsync(ProxyTarget target, CancellationToken cancellationToken);
}

public sealed class SystemTcpConnection : IProxyConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public SystemTcpConnection(TcpClient client, string description)
    {
        _client = client;
        _stream = client.GetStream();
        Description = description;
    }

    public string Description { get; }

    public async ValueTask<int> ReadAsync(byte[] buffer, int count, CancellationToken cancellationToken)
        => await _stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);

    public async ValueTask WriteAsync(byte[] buffer, int count, CancellationToken cancellationToken)
        => await _stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);

    public ValueTask ShutdownWriteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // A reset/closed socket is already terminal for the send side.
        }
        catch (ObjectDisposedException)
        {
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class DirectTcpConnector : IProxyConnector
{
    private readonly TimeSpan _connectTimeout;

    public DirectTcpConnector(TimeSpan connectTimeout)
    {
        _connectTimeout = connectTimeout;
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_connectTimeout);

        var client = new TcpClient {
            NoDelay = true
        };

        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeoutCts.Token).ConfigureAwait(false);
            return new SystemTcpConnection(client, $"direct:{target}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public sealed class LoopbackTcpConnector : IProxyConnector
{
    private readonly TimeSpan _connectTimeout;

    public LoopbackTcpConnector(TimeSpan connectTimeout)
    {
        _connectTimeout = connectTimeout;
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        var loopback = target.TryGetIPAddress(out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_connectTimeout);

        var client = new TcpClient(loopback.AddressFamily) {
            NoDelay = true
        };

        try
        {
            await client.ConnectAsync(loopback, target.Port, timeoutCts.Token).ConfigureAwait(false);
            return new SystemTcpConnection(client, $"loopback:{loopback}:{target.Port}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
