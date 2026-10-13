namespace NetLoop.Host;

/// <summary>
/// Sole owner of a Windows physical-network handover epoch. The native Binder
/// receives this token; it never makes a second 6s/2s episode decision.
/// Injectable monotonic clock keeps startup/Ready bursts deterministic in tests.
/// </summary>
internal sealed class PhysicalHandoverEpisode(Func<long>? clock = null)
{
    private const long MinimumDurationMs = 6_000;
    private const long QuietPeriodMs = 2_000;

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private bool _active;
    private long _startedAt;
    private long _lastNotificationAt;

    internal ulong Epoch { get; private set; }

    /// <returns>True only for the first notification of a new episode.</returns>
    internal bool Observe()
    {
        var now = _clock();
        var continuation = _active
            && (now - _startedAt < MinimumDurationMs
                || now - _lastNotificationAt < QuietPeriodMs);
        if (!continuation)
        {
            _active = true;
            _startedAt = now;
            ++Epoch;
        }

        // Repeated notifications must still reach Native to refresh Binder.
        _lastNotificationAt = now;
        return !continuation;
    }
}
