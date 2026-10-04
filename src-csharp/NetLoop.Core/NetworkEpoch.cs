namespace NetLoop.Core;

public readonly record struct NetworkEpochSnapshot(
    long Value,
    CancellationToken CancellationToken);

public sealed class NetworkEpoch : IDisposable
{
    private readonly object _gate = new();
    private readonly List<CancellationTokenSource> _sources = [];
    private CancellationTokenSource _current = new();
    private long _value;
    private int _disposed;

    public NetworkEpoch()
    {
        _sources.Add(_current);
    }

    public long Value
    {
        get
        {
            lock (_gate)
                return _value;
        }
    }

    public NetworkEpochSnapshot Capture()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new NetworkEpochSnapshot(_value, _current.Token);
        }
    }

    public NetworkEpochSnapshot Advance()
    {
        CancellationTokenSource previous;
        NetworkEpochSnapshot snapshot;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            previous = _current;
            _current = new CancellationTokenSource();
            _sources.Add(_current);
            _value++;
            snapshot = new NetworkEpochSnapshot(_value, _current.Token);
        }

        // Cancellation callbacks may close sockets. Never execute them while
        // holding the epoch state lock.
        previous.Cancel();
        return snapshot;
    }

    public void Dispose()
    {
        List<CancellationTokenSource> sources;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            sources = [.. _sources];
            _sources.Clear();
        }

        foreach (var source in sources)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            source.Dispose();
        }
    }
}
