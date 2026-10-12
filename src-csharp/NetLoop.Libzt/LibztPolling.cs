namespace NetLoop.Libzt;

/// <summary>
/// One maintained per-socket implementation until native fd batching is
/// demonstrated safe and faster on actual devices. No experimental switch.
/// </summary>
public static class LibztPolling
{
    public static object SnapshotMetrics()
    {
        return new {
            mode = "per-socket",
            native_poll_calls = LibztSocketPoller.PollCalls
        };
    }
}
