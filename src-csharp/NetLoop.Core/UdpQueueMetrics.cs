namespace NetLoop.Core;

/// <summary>
/// Process-lifetime UDP backpressure counters. A full queue drops the new
/// datagram instead of blocking a native receive loop or evicting silent
/// older entries. Values are cumulative and safe to query from status writers.
/// </summary>
public static class UdpQueueMetrics
{
    private static long _systemReceiveDropped;
    private static long _routingReceiveDropped;
    private static long _overlayForwardDropped;

    public static void SystemReceiveDropped()
        => Interlocked.Increment(ref _systemReceiveDropped);

    public static void RoutingReceiveDropped()
        => Interlocked.Increment(ref _routingReceiveDropped);

    public static void OverlayForwardDropped()
        => Interlocked.Increment(ref _overlayForwardDropped);

    public static object Snapshot()
        => new {
            system_receive = Interlocked.Read(ref _systemReceiveDropped),
            routing_receive = Interlocked.Read(ref _routingReceiveDropped),
            overlay_forward = Interlocked.Read(ref _overlayForwardDropped)
        };
}
