using NetLoop.Core;

namespace NetLoop.Socks;

/// <summary>
/// Inbound TCP permit occupancy, not a timer or a connection governor.
/// Each permit stays in one phase until the control/relay connection closes.
/// </summary>
public enum TcpSessionPhase
{
    Handshake,
    Connecting,
    Relay,
    UdpAssociation
}

public sealed record TcpSourceUsage(
    string Ingress,
    string Source,
    int Handshake,
    int Connecting,
    int Relay,
    int UdpAssociation)
{
    public int Total => Handshake + Connecting + Relay + UdpAssociation;
}

public sealed record TcpRejectedSourceUsage(string Ingress, string Source, long Rejected);

public sealed record TcpSessionUsage(
    int Total,
    int Handshake,
    int Connecting,
    int Relay,
    int UdpAssociation,
    int Local,
    int Overlay,
    long RejectedLocal,
    long RejectedOverlay,
    IReadOnlyList<TcpSourceUsage> Sources,
    IReadOnlyList<TcpRejectedSourceUsage> RejectedSources,
    long RejectedOther);

/// <summary>
/// Tracks the stage and ZeroTier source of every accepted TCP control
/// connection. The existing Runtime semaphore remains the only permit owner.
/// No long-lived relay is ended by this diagnostic.
/// </summary>
public sealed class TcpSessionTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Lease> _active = [];
    private const int MaxRejectedSources = 32;
    private readonly Dictionary<(string Ingress, string Source), long> _rejectedSources = [];
    private long _rejectedLocal;
    private long _rejectedOverlay;
    private long _rejectedOther;

    public Lease Track(string ingress, string source)
    {
        if (ingress is not ("local" or "overlay"))
            throw new ArgumentOutOfRangeException(nameof(ingress));

        var lease = new Lease(this, ingress, source);
        lock (_gate)
            _active.Add(lease);
        return lease;
    }

    public void Reject(string ingress, string source, int capacity)
    {
        if (ingress == "local")
            Interlocked.Increment(ref _rejectedLocal);
        else if (ingress == "overlay")
            Interlocked.Increment(ref _rejectedOverlay);
        else
            throw new ArgumentOutOfRangeException(nameof(ingress));

        long sourceRejected;
        lock (_gate)
        {
            var key = (ingress, source);
            if (_rejectedSources.TryGetValue(key, out var count))
            {
                sourceRejected = ++count;
                _rejectedSources[key] = count;
            }
            else if (_rejectedSources.Count < MaxRejectedSources)
            {
                sourceRejected = 1;
                _rejectedSources.Add(key, 1);
            }
            else
            {
                sourceRejected = ++_rejectedOther;
            }
        }
        // Rejection storms must not cause another JSONL allocation storm.
        // The status file retains exact counters, including by source.
        if (sourceRejected > 1 && (sourceRejected & (sourceRejected - 1)) != 0)
            return;
        var usage = Snapshot();
        JsonLog.Info("tcp_accept_rejected", new {
            ingress,
            source,
            reason = "max_connections",
            rejected_from_source_or_overflow_group = sourceRejected,
            capacity,
            usage.Total,
            usage.Handshake,
            usage.Connecting,
            usage.Relay,
            usage.UdpAssociation,
            usage.Sources
        });
    }

    public TcpSessionUsage Snapshot()
    {
        lock (_gate)
        {
            var sessions = _active.ToArray();
            var sources = sessions.GroupBy(
                    static session => (session.Ingress, session.Source))
                .Select(group => new TcpSourceUsage(
                    group.Key.Ingress,
                    group.Key.Source,
                    group.Count(static session => session.Phase == TcpSessionPhase.Handshake),
                    group.Count(static session => session.Phase == TcpSessionPhase.Connecting),
                    group.Count(static session => session.Phase == TcpSessionPhase.Relay),
                    group.Count(static session => session.Phase == TcpSessionPhase.UdpAssociation)))
                .OrderByDescending(static source => source.Total)
                .ThenBy(static source => source.Ingress)
                .ThenBy(static source => source.Source)
                .ToArray();

            return new TcpSessionUsage(
                sessions.Length,
                sessions.Count(static x => x.Phase == TcpSessionPhase.Handshake),
                sessions.Count(static x => x.Phase == TcpSessionPhase.Connecting),
                sessions.Count(static x => x.Phase == TcpSessionPhase.Relay),
                sessions.Count(static x => x.Phase == TcpSessionPhase.UdpAssociation),
                sessions.Count(static x => x.Ingress == "local"),
                sessions.Count(static x => x.Ingress == "overlay"),
                Interlocked.Read(ref _rejectedLocal),
                Interlocked.Read(ref _rejectedOverlay),
                sources,
                _rejectedSources.Select(static item => new TcpRejectedSourceUsage(
                        item.Key.Ingress, item.Key.Source, item.Value))
                    .OrderByDescending(static item => item.Rejected)
                    .ToArray(),
                _rejectedOther);
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly TcpSessionTracker _owner;
        private bool _disposed;

        internal Lease(TcpSessionTracker owner, string ingress, string source)
        {
            _owner = owner;
            Ingress = ingress;
            Source = source;
        }

        internal string Ingress { get; }
        internal string Source { get; }
        internal TcpSessionPhase Phase { get; private set; } = TcpSessionPhase.Handshake;

        public void SetPhase(TcpSessionPhase phase)
        {
            lock (_owner._gate)
            {
                if (!_disposed)
                    Phase = phase;
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _owner._active.Remove(this);
            }
        }
    }
}
