using NetLoop.Core;
using NetLoop.Libzt;
using NetLoop.Socks;

namespace NetLoop.Host;

internal sealed class OverlaySocksAgent : IAsyncDisposable
{
    private readonly LibztTcpListener _listener;
    private readonly Socks5ConnectionHandler _handler;
    private readonly SemaphoreSlim _capacity;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _clientsGate = new();
    private readonly HashSet<Task> _clients = [];
    private readonly HashSet<LibztTcpConnection> _activeClients = [];
    private readonly Task _loop;
    private int _aborted;
    private int _disposed;

    internal OverlaySocksAgent(
        string bindAddress,
        ushort port,
        Socks5ConnectionHandler handler,
        int maxConnections)
    {
        _handler = handler;
        _capacity = new SemaphoreSlim(maxConnections, maxConnections);
        _listener = LibztTcpListener.Start(bindAddress, port, maxConnections);
        _loop = AcceptLoopAsync(_stop.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
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

                JsonLog.Error("overlay_accept_failed", new { error = ex.Message });
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!await _capacity.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            lock (_clientsGate)
                _activeClients.Add(connection);
            TrackClient(HandleClientAsync(connection, cancellationToken), connection);
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
            JsonLog.Error("overlay_socks_client_failed", new { error = ex.Message });
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
        _capacity.Dispose();
    }
}
