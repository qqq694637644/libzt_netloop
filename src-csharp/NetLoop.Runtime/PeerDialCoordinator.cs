using System.Net;
using NetLoop.Core;

namespace NetLoop.Host;

/// <summary>
/// Small per-Peer admission limit across independent inbound SOCKS requests.
/// Holds a permit only through overlay TCP connect and SOCKS5 greeting,
/// never through the target CONNECT reply or established RDP stream.
/// No background tasks.
/// </summary>
internal sealed class PeerDialCoordinator
{
    internal const int MaxInFlightPerPeer = 2;
    internal const int MaxWaitingPerPeer = 4;
    internal const int MaxTrackedPeers = 32;
    internal const int AdmissionWaitMilliseconds = 3000;
    internal const int MaxCooldownMilliseconds = 2000;

    private sealed class PeerState
    {
        internal readonly SemaphoreSlim Slots = new(MaxInFlightPerPeer, MaxInFlightPerPeer);
        internal int Waiting;
        internal int InFlight;
        internal int Failures;
        internal long RetryAfter;
        internal long LastUsed;
        internal long Rejected;
    }

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, PeerState> _peers = [];
    private readonly Func<long> _clock;

    internal PeerDialCoordinator(Func<long>? clock = null)
        => _clock = clock ?? (() => Environment.TickCount64);

    internal sealed record PeerDialStatus(
        string Peer,
        int InFlight,
        int Waiting,
        int ConsecutiveFailures,
        int RemainingCooldownMs,
        long Rejected);

    internal PeerDialStatus[] Snapshot()
    {
        lock (_gate)
            return _peers.Select(pair => StatusUnderLock(pair.Key, pair.Value))
                .OrderBy(static item => item.Peer, StringComparer.Ordinal)
                .ToArray();
    }

    private PeerDialStatus StatusUnderLock(IPAddress peer, PeerState state)
        => new(
            peer.ToString(), state.InFlight, state.Waiting, state.Failures,
            (int)Math.Clamp(state.RetryAfter - _clock(), 0, MaxCooldownMilliseconds),
            state.Rejected);

    private static bool ShouldLogRejection(long count)
        => count <= 1 || (count & (count - 1)) == 0;

    internal async ValueTask<IProxyConnection> ConnectAsync(
        IPAddress peer,
        Func<CancellationToken, ValueTask<IProxyConnection>> connect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PeerState? state;
        string? rejectReason = null;
        PeerDialStatus? rejectedStatus = null;
        var rejectedCount = 0L;
        lock (_gate)
        {
            var now = _clock();
            if (!_peers.TryGetValue(peer, out state))
            {
                // Never retain an ever-growing dictionary of arbitrary
                // Controller peer IPs. Only inactive entries are evicted.
                if (_peers.Count >= MaxTrackedPeers)
                {
                    var idle = _peers
                        .Where(static item => item.Value.InFlight == 0
                            && item.Value.Waiting == 0)
                        .OrderBy(static item => item.Value.LastUsed)
                        .FirstOrDefault();
                    if (idle.Value is not null)
                        _peers.Remove(idle.Key);
                }
                if (_peers.Count >= MaxTrackedPeers)
                    rejectReason = "peer_table_full";
                else
                {
                    state = new PeerState { LastUsed = now };
                    _peers.Add(peer, state);
                }
            }

            if (rejectReason is null && state is not null)
            {
                if (state.Waiting >= MaxWaitingPerPeer)
                {
                    state.Rejected++;
                    rejectedCount = state.Rejected;
                    rejectReason = "peer_queue_full";
                    rejectedStatus = StatusUnderLock(peer, state);
                }
                else
                {
                    state.Waiting++;
                }
            }
        }

        if (rejectReason is not null || state is null)
        {
            if (ShouldLogRejection(rejectedCount))
                JsonLog.Info("peer_dial_rejected", new {
                    peer = peer.ToString(),
                    reason = rejectReason,
                    status = rejectedStatus
                });
            throw new IOException($"Overlay Peer {peer} admission rejected: {rejectReason}.");
        }

        var acquired = false;
        var inFlight = false;
        try
        {
            acquired = await state.Slots.WaitAsync(
                TimeSpan.FromMilliseconds(AdmissionWaitMilliseconds),
                cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                long count;
                lock (_gate)
                    count = ++state.Rejected;
                if (ShouldLogRejection(count))
                    JsonLog.Info("peer_dial_rejected", new {
                        peer = peer.ToString(),
                        reason = "peer_queue_timeout",
                        rejected_total = count
                    });
                throw new TimeoutException($"Overlay Peer {peer} admission waited too long.");
            }

            long remaining;
            lock (_gate)
            {
                state.Waiting--;
                state.InFlight++;
                inFlight = true;
                state.LastUsed = _clock();
                remaining = Math.Clamp(
                    state.RetryAfter - _clock(), 0, MaxCooldownMilliseconds);
            }

            if (remaining > 0)
            {
                JsonLog.Info("peer_dial_cooldown", new {
                    peer = peer.ToString(),
                    delay_ms = remaining
                });
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken)
                    .ConfigureAwait(false);
            }

            // Returning from ConnectAsync means the Peer transport and SOCKS
            // greeting succeeded, not the final target CONNECT reply.
            try
            {
                var connection = await connect(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        JsonLog.Error("peer_dial_cancel_cleanup_failed", new {
                            peer = peer.ToString(),
                            error_type = ex.GetType().Name
                        });
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                lock (_gate)
                {
                    state.Failures = 0;
                    state.RetryAfter = 0;
                }
                return connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                      || !cancellationToken.IsCancellationRequested)
            {
                // Bad credentials, unsupported protocol and local programmer
                // errors are not network churn and must not start cooldown.
                if (ex is not (UnauthorizedAccessException
                    or ProtocolViolationException
                    or ArgumentException
                    or OperationCanceledException))
                {
                    int delay;
                    int failures;
                    lock (_gate)
                    {
                        state.Failures = Math.Min(state.Failures + 1, 4);
                        failures = state.Failures;
                        delay = Math.Min(250 << (failures - 1), MaxCooldownMilliseconds);
                        state.RetryAfter = Math.Max(state.RetryAfter, _clock() + delay);
                    }
                    JsonLog.Info("peer_dial_failure_backoff", new {
                        peer = peer.ToString(),
                        failure_streak = failures,
                        cooldown_ms = delay,
                        error_type = ex.GetType().Name
                    });
                }
                throw;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (inFlight)
                    state.InFlight--;
                else
                    state.Waiting--;
                state.LastUsed = _clock();
            }
            if (acquired)
                state.Slots.Release();
        }
    }
}
