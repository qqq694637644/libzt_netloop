using System.Net;
using System.Net.Sockets;
using NetLoop.Core;

namespace NetLoop.Socks;

public sealed class LocalSocksServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Socks5ConnectionHandler _handler;
    private readonly SemaphoreSlim _capacity;
    private readonly TcpSessionTracker _sessions;
    private readonly int _maxConnections;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _clientsGate = new();
    private readonly HashSet<Task> _clients = [];
    private readonly HashSet<TcpClient> _activeClients = [];
    private Task? _loop;
    private int _aborted;
    private int _disposed;

    public LocalSocksServer(
        IPEndPoint listenEndPoint,
        Socks5ConnectionHandler handler,
        SemaphoreSlim capacity,
        TcpSessionTracker sessions,
        int maxConnections)
    {
        _listener = new TcpListener(listenEndPoint);
        _handler = handler;
        _capacity = capacity;
        _sessions = sessions;
        _maxConnections = maxConnections;
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public Task Completion => _loop
        ?? throw new InvalidOperationException("SOCKS5 server has not started.");

    public void Start()
    {
        if (_loop is not null)
            throw new InvalidOperationException("SOCKS5 server is already started.");

        _listener.Start(512);
        JsonLog.Info("local_socks_ready", new { endpoint = EndPoint.ToString() });
        _loop = AcceptLoopAsync(_stop.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            bool accepted;
            try
            {
                accepted = await _capacity.WaitAsync(0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                break;
            }
            if (!accepted)
            {
                var source = (client.Client.RemoteEndPoint as IPEndPoint)?
                    .Address.ToString() ?? "unknown";
                _sessions.Reject("local", source, _maxConnections);
                client.Dispose();
                continue;
            }

            lock (_clientsGate)
                _activeClients.Add(client);
            TrackClient(HandleClientAsync(client, cancellationToken), client);
        }
    }

    private void TrackClient(Task task, TcpClient client)
    {
        lock (_clientsGate)
            _clients.Add(task);
        _ = ObserveClientAsync(task, client);
    }

    private async Task ObserveClientAsync(Task task, TcpClient client)
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
                _activeClients.Remove(client);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var usage = _sessions.Track(
            "local",
            (client.Client.RemoteEndPoint as IPEndPoint)?
                .Address.ToString() ?? "unknown");
        await using var connection = new SystemTcpConnection(
            client,
            $"local:{client.Client.RemoteEndPoint}");
        try
        {
            await _handler.HandleAsync(
                connection, cancellationToken, usage.SetPhase).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            JsonLog.Error("local_socks_client_failed", new { error = ex.Message });
        }
        finally
        {
            _capacity.Release();
        }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
            return;

        _stop.Cancel();
        _listener.Stop();

        TcpClient[] activeClients;
        lock (_clientsGate)
            activeClients = [.. _activeClients];
        foreach (var client in activeClients)
            client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Abort();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] activeClients;
        lock (_clientsGate)
            activeClients = [.. _clients];
        if (activeClients.Length != 0)
            await Task.WhenAll(activeClients).ConfigureAwait(false);

        _stop.Dispose();
        // The runtime owns the capacity shared with the overlay listener.
    }
}
