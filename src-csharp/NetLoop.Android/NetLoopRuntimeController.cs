using Android.Content;

namespace NetLoop.Android;

internal enum NetLoopPublicState
{
    Stopped,
    Starting,
    Ready
}

internal sealed record NetLoopPublicStatus(
    NetLoopPublicState State,
    string? NodeId,
    string? PrimaryOverlayAddress);

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
    private static string? _primaryOverlayAddress;

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
            if (_state is NetLoopPublicState.Starting or NetLoopPublicState.Ready)
            {
                if (_controlledConfig is not null
                    && _controlledConfig.IsEquivalentTo(desired))
                {
                    return new NetLoopControlResult(
                        true,
                        SnapshotLocked(),
                        null);
                }

                return new NetLoopControlResult(
                    false,
                    SnapshotLocked(),
                    "stop before changing configuration");
            }

            _controlledConfig = desired;
            _state = NetLoopPublicState.Starting;
            _nodeId = null;
            _primaryOverlayAddress = null;
        }

        try
        {
            var intent = new Intent(context, typeof(NetLoopService))
                .SetAction(NetLoopService.ActionStartControlled);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);

            return new NetLoopControlResult(
                true,
                GetStatus(),
                null);
        }
        catch (Exception ex)
        {
            ReportStopped();
            return new NetLoopControlResult(
                false,
                GetStatus(),
                ex.Message);
        }
    }

    internal static bool Stop(Context context)
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Stopped;
            _controlledConfig = null;
            _nodeId = null;
            _primaryOverlayAddress = null;
        }

        return context.StopService(new Intent(context, typeof(NetLoopService)));
    }

    internal static void ReportRuntimeStarting()
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Starting;
            _nodeId = null;
            _primaryOverlayAddress = null;
        }
    }

    internal static ControlledRuntimeConfig GetControlledConfigForService()
    {
        lock (Gate)
        {
            if (_controlledConfig is null)
            {
                throw new InvalidOperationException(
                    "Controlled NetLoop runtime configuration is unavailable.");
            }

            return _controlledConfig;
        }
    }

    internal static void ReportRuntimeRestarting()
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Starting;
            _nodeId = null;
            _primaryOverlayAddress = null;
        }
    }

    internal static void ReportReady(
        ulong nodeId,
        string primaryOverlayAddress)
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Ready;
            _nodeId = nodeId.ToString("x10");
            _primaryOverlayAddress = primaryOverlayAddress;
        }
    }

    internal static void ReportStopped()
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Stopped;
            _controlledConfig = null;
            _nodeId = null;
            _primaryOverlayAddress = null;
        }
    }

    private static NetLoopPublicStatus SnapshotLocked()
        => new(
            _state,
            _nodeId,
            _primaryOverlayAddress);
}
