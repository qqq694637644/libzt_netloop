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
    private readonly TcpSessionTracker _sessions;
    private readonly int _maxConnections;
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
        SemaphoreSlim capacity,
        TcpSessionTracker sessions,
        AgentAccessPolicy accessPolicy)
    {
        _handler = handler;
        _accessPolicy = accessPolicy;
        _capacity = capacity;
        _sessions = sessions;
        _maxConnections = maxConnections;
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

            bool accepted;
            try
            {
                accepted = await _capacity.WaitAsync(0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                break;
            }
            if (!accepted)
            {
                _sessions.Reject(
                    "overlay", source?.ToString() ?? "unknown", _maxConnections);
                await connection.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            lock (_clientsGate)
                _activeClients.Add(connection);
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
        using var usage = _sessions.Track(
            "overlay",
            (connection.RemoteEndPoint as System.Net.IPEndPoint)?
                .Address.ToString() ?? "unknown",
            _capacity);
        await using var client = connection;
        try
        {
            await _handler.HandleAsync(
                client, cancellationToken, usage.SetPhase).ConfigureAwait(false);
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
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        _stop.Cancel();
        _acceptStop.Cancel();
        _ = _listener.DisposeAsync();

        LibztTcpConnection[] activeClients;
        lock (_clientsGate)
            activeClients = [.. _activeClients];
        foreach (var connection in activeClients)
            _ = connection.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Abort();
        await _listener.DisposeAsync().ConfigureAwait(false);

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
        // The Runtime disposes the shared capacity after both listeners stop.
    }
}
