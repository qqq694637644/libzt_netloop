using Android.Content;
using NetLoop.Host;

namespace NetLoop.Android;

internal enum NetLoopPublicState
{
    Stopped,
    Starting,
    Ready,
    Degraded,
    Resetting
}

internal sealed record NetLoopPublicStatus(
    NetLoopPublicState State,
    string? NodeId,
    string? PrimaryOverlayAddress,
    string? Reason,
    long? RouteGeneration,
    DateTimeOffset UpdatedAtUtc);

internal sealed record NetLoopControlResult(
    bool Success,
    NetLoopPublicStatus Status,
    string? Error);

internal static class NetLoopRuntimeController
{
    private static readonly object Gate = new();
    private static NetLoopPublicState _state = NetLoopPublicState.Stopped;
    private static ControlledRuntimeConfig? _controlledConfig;
    private static string? _nodeId;
    private static string? _address;
    private static string? _reason;
    private static long? _generation;
    private static DateTimeOffset _updatedAtUtc = DateTimeOffset.UtcNow;

    internal static NetLoopPublicStatus GetStatus()
    {
        lock (Gate)
            return SnapshotLocked();
    }

    internal static NetLoopControlResult StartControlled(
        Context context,
        ControlledRuntimeConfig desired)
    {
        lock (Gate)
        {
            if (_state != NetLoopPublicState.Stopped)
            {
                if (_controlledConfig is not null
                    && _controlledConfig.IsEquivalentTo(desired))
                    return new NetLoopControlResult(true, SnapshotLocked(), null);

                return new NetLoopControlResult(
                    false, SnapshotLocked(), "stop before changing configuration");
            }
            _controlledConfig = desired;
            UpdateLocked(NetLoopPublicState.Starting, null, null, null, null);
        }

        try
        {
            var intent = new Intent(context, typeof(NetLoopService))
                .SetAction(NetLoopService.ActionStartControlled);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
            return new NetLoopControlResult(true, GetStatus(), null);
        }
        catch (Exception ex)
        {
            ReportStopped();
            return new NetLoopControlResult(false, GetStatus(), ex.Message);
        }
    }

    internal static bool Stop(Context context)
    {
        ReportStopped();
        return context.StopService(new Intent(context, typeof(NetLoopService)));
    }

    internal static void ReportRuntimeStarting()
    {
        lock (Gate)
            UpdateLocked(NetLoopPublicState.Starting, null, null, null, null);
    }

    internal static ControlledRuntimeConfig GetControlledConfigForService()
    {
        lock (Gate)
            return _controlledConfig
                ?? throw new InvalidOperationException(
                    "Controlled NetLoop runtime configuration is unavailable.");
    }

    internal static void ReportRuntimeStatus(RuntimeStatus status)
    {
        lock (Gate)
        {
            if (_state == NetLoopPublicState.Stopped)
                return;
            var next = status.Phase switch {
                RuntimePhase.Ready => NetLoopPublicState.Ready,
                RuntimePhase.Degraded => NetLoopPublicState.Degraded,
                RuntimePhase.Resetting => NetLoopPublicState.Resetting,
                RuntimePhase.Starting => NetLoopPublicState.Starting,
                _ => NetLoopPublicState.Stopped
            };
            UpdateLocked(
                next,
                status.Network?.NodeId.ToString("x10"),
                status.Runtime?.OverlayBindAddress.ToString(),
                status.Reason,
                status.Network?.Generation);
        }
    }

    internal static void ReportStopped()
    {
        lock (Gate)
        {
            _controlledConfig = null;
            UpdateLocked(NetLoopPublicState.Stopped, null, null, null, null);
        }
    }

    private static void UpdateLocked(
        NetLoopPublicState state,
        string? nodeId,
        string? address,
        string? reason,
        long? generation)
    {
        _state = state;
        _nodeId = nodeId;
        _address = address;
        _reason = reason;
        _generation = generation;
        _updatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static NetLoopPublicStatus SnapshotLocked()
        => new(
            _state,
            _nodeId,
            _address,
            _reason,
            _generation,
            _updatedAtUtc);
}
