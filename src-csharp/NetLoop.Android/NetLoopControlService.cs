using Android.App;
using Android.Content;
using Android.OS;

namespace NetLoop.Android;

[Service(
    Name = "com.libzt.netloop.NetLoopControlService",
    Exported = true,
    Process = ":netloop")]
public sealed class NetLoopControlService : Service
{
    internal const int ControlApiVersion = 1;
    internal const int CommandStart = 1;
    internal const int CommandStop = 2;
    internal const int CommandGetStatus = 3;

    private const string AllowedPackage = "com.v2ray.ang";
    private Messenger? _messenger;

    public override void OnCreate()
    {
        base.OnCreate();
        _messenger = new Messenger(
            new ControlHandler(this, Looper.MainLooper!));
    }

    public override IBinder? OnBind(Intent? intent)
        => _messenger?.Binder;

    private bool IsAllowedCaller(int uid)
    {
        if (uid < 0)
            return false;

        var packages = PackageManager?.GetPackagesForUid(uid);
        return packages?.Any(
                   package => string.Equals(
                       package,
                       AllowedPackage,
                       StringComparison.Ordinal)) == true;
    }

    private void HandleControlMessage(Message message)
    {
        if (!IsAllowedCaller(message.SendingUid))
        {
            ReplyError(message, "unauthorized caller");
            return;
        }

        try
        {
            switch (message.What)
            {
                case CommandGetStatus:
                    ReplyStatus(message, NetLoopRuntimeController.GetStatus());
                    break;

                case CommandStart:
                    HandleStart(message);
                    break;

                case CommandStop:
                    ReplyStatus(
                        message,
                        new NetLoopPublicStatus(
                            NetLoopPublicState.Stopped,
                            null,
                            null,
                            null));
                    new Handler(Looper.MainLooper!).Post(
                        () => NetLoopRuntimeController.Stop(this));
                    break;

                default:
                    ReplyError(message, $"unknown command: {message.What}");
                    break;
            }
        }
        catch (Exception ex)
        {
            ReplyError(
                message,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? ex.GetType().Name
                    : ex.Message);
        }
    }

    private void HandleStart(Message message)
    {
        var data = message.Data ?? new Bundle();
        if (data.GetInt("api_version", -1) != ControlApiVersion)
        {
            ReplyError(message, "NetLoop control API version mismatch.");
            return;
        }

        var networkId = data.GetString("network_id") ?? string.Empty;
        var defaultExit = data.GetString("default_exit") ?? string.Empty;
        var peerValues = data.GetStringArrayList("peers");
        var peers = peerValues is null
            ? Array.Empty<string>()
            : peerValues
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToArray();

        var config = ControlledRuntimeConfig.Parse(
            networkId,
            defaultExit,
            peers);
        var result = NetLoopRuntimeController.StartControlled(this, config);
        if (!result.Success)
        {
            ReplyError(
                message,
                result.Error ?? "NetLoop START failed.",
                result.Status);
            return;
        }

        ReplyStatus(message, result.Status);
    }

    private static void ReplyStatus(
        Message request,
        NetLoopPublicStatus status)
    {
        var reply = Message.Obtain()
            ?? throw new InvalidOperationException("Unable to allocate Android message.");
        reply.What = request.What;
        reply.Arg1 = request.Arg1;
        reply.Data = BuildStatusBundle(status, true, null);
        SendReply(request, reply);
    }

    private static void ReplyError(
        Message request,
        string error,
        NetLoopPublicStatus? status = null)
    {
        var reply = Message.Obtain()
            ?? throw new InvalidOperationException("Unable to allocate Android message.");
        reply.What = request.What;
        reply.Arg1 = request.Arg1;
        reply.Data = BuildStatusBundle(
            status ?? NetLoopRuntimeController.GetStatus(),
            false,
            error);
        SendReply(request, reply);
    }

    private static void SendReply(Message request, Message reply)
    {
        try
        {
            request.ReplyTo?.Send(reply);
        }
        catch (RemoteException)
        {
            // The caller disappeared after sending the request. The command
            // result remains valid; there is nobody left to receive it.
        }
        finally
        {
            reply.Dispose();
        }
    }

    private static Bundle BuildStatusBundle(
        NetLoopPublicStatus status,
        bool ok,
        string? error)
    {
        var data = new Bundle();
        data.PutBoolean("ok", ok);
        data.PutInt("api_version", ControlApiVersion);
        data.PutString(
            "state",
            status.State switch {
                NetLoopPublicState.Starting => "STARTING",
                NetLoopPublicState.Ready => "READY",
                NetLoopPublicState.Error => "ERROR",
                _ => "STOPPED"
            });
        data.PutString("node_id", status.NodeId);
        data.PutString(
            "primary_overlay_address",
            status.PrimaryOverlayAddress);
        data.PutString("last_error", status.LastError);
        data.PutString("error", error);
        return data;
    }

    private sealed class ControlHandler(
        NetLoopControlService service,
        Looper looper) : Handler(looper)
    {
        public override void HandleMessage(Message message)
            => service.HandleControlMessage(message);
    }
}
