using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class OverlaySocksAgent : IAsyncDisposable
{
    private readonly LibztTcpListener _listener;
    private readonly Socks5ConnectionHandler _handler;
    private readonly AgentAccessPolicy _accessPolicy;
    private readonly SemaphoreSlim _capacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _acceptStop;
    private readonly object _clientsGate = new();
    private readonly HashSet<Task> _clients = [];
    private readonly HashSet<LibztTcpConnection> _activeClients = [];
    private readonly Task _loop;
    private int _aborted;
    private int _disposed;

    internal Task Completion => _loop;

    internal OverlaySocksAgent(
        string bindAddress,
        ushort port,
        Socks5ConnectionHandler handler,
        int maxConnections,
        AgentAccessPolicy accessPolicy)
    {
        _handler = handler;
        _accessPolicy = accessPolicy;
        _capacity = new SemaphoreSlim(maxConnections, maxConnections);
        _listener = LibztTcpListener.Start(bindAddress, port, maxConnections);
        _acceptStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _loop = AcceptLoopAsync(_acceptStop.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            LibztTcpConnection connection;
            try
            {
                connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                consecutiveFailures++;
                JsonLog.Error("overlay_accept_failed", new { error = ex.Message });
                // A listener repeatedly failing accept() is not healthy,
                // even if the retry task itself is still technically alive.
                if (consecutiveFailures >= 30)
                    throw new IOException(
                        "Overlay TCP accept failed 30 consecutive times.", ex);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            consecutiveFailures = 0;
            var source = (connection.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
            if (!_accessPolicy.AllowsSource(source))
            {
                JsonLog.Info("agent_source_denied", new {
                    protocol = "tcp",
                    connection_id = connection.ConnectionId,
                    source = connection.RemoteEndPoint?.ToString()
                });
                await connection.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (!await _capacity.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                JsonLog.Info("tcp_accept_rejected", new {
                    connection_id = connection.ConnectionId,
                    client = connection.Description,
                    reason = "max_connections"
                });
                await connection.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            lock (_clientsGate)
                _activeClients.Add(connection);
            // Accept can be stopped when the Controller reassigns our
            // Managed IP, but existing TCP sessions must keep their original
            // runtime lifetime until they close naturally.
            TrackClient(HandleClientAsync(connection, _stop.Token), connection);
        }
    }

    private void TrackClient(Task task, LibztTcpConnection connection)
    {
        lock (_clientsGate)
            _clients.Add(task);
        _ = ObserveClientAsync(task, connection);
    }

    private async Task ObserveClientAsync(
        Task task,
        LibztTcpConnection connection)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_clientsGate)
            {
                _clients.Remove(task);
                _activeClients.Remove(connection);
            }
        }
    }

    private async Task HandleClientAsync(
        LibztTcpConnection connection,
        CancellationToken cancellationToken)
    {
        await using var client = connection;
        try
        {
            await _handler.HandleAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("overlay_socks_client_failed", new {
                connection_id = connection.ConnectionId,
                client = connection.Description,
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
        finally
        {
            _capacity.Release();
        }
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        _stop.Cancel();
        StopAccepting();

        LibztTcpConnection[] activeClients;
        lock (_clientsGate)
            activeClients = [.. _activeClients];
        foreach (var connection in activeClients)
            _ = connection.DisposeAsync();
    }

    /// <summary>
    /// Retire the old listening IP without canceling already accepted TCP
    /// connections. The caller may start a replacement Agent on a new IP.
    /// </summary>
    internal void StopAccepting()
    {
        if (!_acceptStop.IsCancellationRequested)
            _acceptStop.Cancel();
        _ = _listener.DisposeAsync();
    }

    internal async Task DrainAndDisposeAsync()
    {
        StopAccepting();
        await _loop.ConfigureAwait(false);
        Task[] clients;
        lock (_clientsGate)
            clients = [.. _clients];
        if (clients.Length != 0)
            await Task.WhenAll(clients).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Abort();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] activeClients;
        lock (_clientsGate)
            activeClients = [.. _clients];
        if (activeClients.Length != 0)
            await Task.WhenAll(activeClients).ConfigureAwait(false);

        _stop.Dispose();
        _acceptStop.Dispose();
        _capacity.Dispose();
    }
}
