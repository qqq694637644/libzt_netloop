namespace NetLoop.Libzt;

/// <summary>
/// Explicit opt-in for the shared batched socket poller. Set once before the
/// Node starts; per-socket polling remains the default pending real-device
/// long-connection validation.
/// </summary>
public static class LibztPolling
{
    public static void ConfigureShared(bool enabled)
        => LibztSocketPoller.ConfigureShared(enabled);

    public static object SnapshotMetrics()
    {
        var metrics = SharedLibztSocketPoller.Metrics;
        return new {
            mode = LibztSocketPoller.SharedMode ? "shared" : "per-socket",
            shared_waiters = metrics.Waiting,
            shared_poll_calls = metrics.Calls,
            shared_fd_observations = metrics.FdObservations
        };
    }
}
