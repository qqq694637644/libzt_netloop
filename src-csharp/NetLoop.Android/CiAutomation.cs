#if NETLOOP_CI
using System.Text.Json;
using Android.App;
using Android.Content;
using NetLoop.Libzt;

namespace NetLoop.Android;

[BroadcastReceiver(
    Name = "com.libzt.netloop.CiAutomationReceiver",
    Enabled = true,
    Exported = true)]
public sealed class CiAutomationReceiver : BroadcastReceiver
{
    internal const string ActionReset =
        "com.libzt.netloop.ci.RESET";
    internal const string ActionUdpProbe =
        "com.libzt.netloop.ci.UDP_PROBE";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
            return;

        if (string.Equals(
                intent.Action,
                ActionReset,
                StringComparison.Ordinal))
        {
            context.StartService(
                new Intent(context, typeof(NetLoopService))
                    .SetAction(NetLoopService.ActionCiReset));
            return;
        }

        if (string.Equals(
                intent.Action,
                ActionUdpProbe,
                StringComparison.Ordinal))
        {
            CiUdpProbeRequest? request = null;
            Exception? requestError = null;
            try
            {
                request = CiUdpProbeRequest.FromIntent(intent);
            }
            catch (Exception ex)
            {
                requestError = ex;
            }

            var pending = GoAsync()
                ?? throw new InvalidOperationException(
                    "Unable to keep CI UDP broadcast alive.");
            var applicationContext =
                context.ApplicationContext ?? context;
            var requestId = (
                intent.GetStringExtra("request_id")
                ?? "unknown")
                .Trim();
            var mode = (
                intent.GetStringExtra("mode")
                ?? "unknown")
                .Trim();

            _ = Task.Run(
                () =>
                {
                    CiUdpProbeResult result;
                    if (requestError is not null)
                    {
                        result = new CiUdpProbeResult {
                            request_id = requestId,
                            mode = mode,
                            success = false,
                            error_type =
                                requestError.GetType().FullName,
                            error = requestError.Message
                        };
                    }
                    else
                    {
                        result = CiUdpProbe.Run(
                            request
                            ?? throw new InvalidOperationException(
                                "CI UDP probe request is unavailable."));
                    }

                    try
                    {
                        CiUdpProbeStatus.Write(
                            applicationContext,
                            result);
                    }
                    finally
                    {
                        pending.Finish();
                    }
                });
        }
    }

}

internal static class CiAutomationStatus
{
    private const string FileName = "netloop-ci-status.json";

    internal static string GetExternalPath(Context context)
    {
        var directory = context.GetExternalFilesDir(null)
            ?? throw new InvalidOperationException(
                "Android external files directory is unavailable.");
        return Path.Combine(directory.AbsolutePath, FileName);
    }

    internal static void Delete(Context context)
    {
        try
        {
            File.Delete(GetExternalPath(context));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void WriteReady(
        Context context,
        LibztNetworkState state,
        int resetCount,
        string? resetReason)
    {
        Write(
            context,
            new {
                phase = "ready",
                node_id = state.NodeId.ToString("x10"),
                managed_addresses =
                    state.ManagedAddresses
                        .Select(static address => address.ToString())
                        .ToArray(),
                reset_count = resetCount,
                reset_reason = resetReason,
                process_id = global::Android.OS.Process.MyPid()
            });
    }

    internal static void WriteError(Context context, Exception error)
    {
        Write(
            context,
            new {
                phase = "error",
                error_type = error.GetType().FullName,
                error = error.Message,
                process_id = global::Android.OS.Process.MyPid()
            });
    }

    private static void Write(Context context, object payload)
    {
        var path = GetExternalPath(context);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Unable to resolve NetLoop CI status directory.");
        Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
        File.Move(temp, path, true);
    }
}
#endif
