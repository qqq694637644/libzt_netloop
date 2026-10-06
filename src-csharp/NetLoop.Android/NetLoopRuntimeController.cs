using Android.Content;

namespace NetLoop.Android;

internal enum NetLoopPublicState
{
    Stopped,
    Starting,
    Ready,
    Error
}

internal sealed record NetLoopPublicStatus(
    NetLoopPublicState State,
    string? NodeId,
    string? PrimaryOverlayAddress,
    string? LastError);

internal sealed record NetLoopControlResult(
    bool Success,
    NetLoopPublicStatus Status,
    string? Error);

internal static class NetLoopRuntimeController
{
    private static readonly object Gate = new();

    private static NetLoopPublicState _state = NetLoopPublicState.Stopped;
    private static ControlledRuntimeConfig? _controlledConfig;
    private static bool _standaloneRuntime;
    private static string? _nodeId;
    private static string? _primaryOverlayAddress;
    private static string? _lastError;

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
                if (!_standaloneRuntime
                    && _controlledConfig is not null
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

            desired.Save(context);
            _controlledConfig = desired;
            _standaloneRuntime = false;
            _state = NetLoopPublicState.Starting;
            _nodeId = null;
            _primaryOverlayAddress = null;
            _lastError = null;
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
            ReportError(ex);
            return new NetLoopControlResult(
                false,
                GetStatus(),
                ex.Message);
        }
    }

    internal static void Stop(Context context)
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Stopped;
            _controlledConfig = null;
            _standaloneRuntime = false;
            _nodeId = null;
            _primaryOverlayAddress = null;
            _lastError = null;
        }

        context.StopService(new Intent(context, typeof(NetLoopService)));
    }

    internal static void ReportRuntimeStarting(
        ControlledRuntimeConfig? controlledConfig)
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Starting;
            _controlledConfig = controlledConfig;
            _standaloneRuntime = controlledConfig is null;
            _nodeId = null;
            _primaryOverlayAddress = null;
            _lastError = null;
        }
    }

    internal static void ReportRuntimeRestarting()
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Starting;
            _nodeId = null;
            _primaryOverlayAddress = null;
            _lastError = null;
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
            _lastError = null;
        }
    }

    internal static void ReportStopped()
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Stopped;
            _controlledConfig = null;
            _standaloneRuntime = false;
            _nodeId = null;
            _primaryOverlayAddress = null;
            _lastError = null;
        }
    }

    internal static void ReportError(Exception error)
    {
        lock (Gate)
        {
            _state = NetLoopPublicState.Error;
            _nodeId = null;
            _primaryOverlayAddress = null;
            _lastError = string.IsNullOrWhiteSpace(error.Message)
                ? error.GetType().Name
                : error.Message;
        }
    }

    private static NetLoopPublicStatus SnapshotLocked()
        => new(
            _state,
            _nodeId,
            _primaryOverlayAddress,
            _lastError);
}
