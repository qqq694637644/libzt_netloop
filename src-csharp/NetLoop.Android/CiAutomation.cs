#if NETLOOP_CI
using System.Text.Json;
using Android.App;
using Android.Content;
using NetLoop.Host;
using NetLoop.Libzt;

namespace NetLoop.Android;

[BroadcastReceiver(
    Name = "com.libzt.netloop.CiAutomationReceiver",
    Enabled = true,
    Exported = true)]
public sealed class CiAutomationReceiver : BroadcastReceiver
{
    internal const string ActionStart =
        "com.libzt.netloop.ci.START";
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
                ActionStart,
                StringComparison.Ordinal))
        {
            CiAutomationStatus.Delete(context);
            var serviceIntent = new Intent(context, typeof(NetLoopService))
                .SetAction(NetLoopService.ActionCiStart);
            if (intent.Extras is not null)
                serviceIntent.PutExtras(intent.Extras);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
            return;
        }

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

internal static class CiAutomationOptions
{
    internal static HostOptions FromIntent(Context context, Intent intent)
    {
        var networkId = (intent.GetStringExtra("network_id") ?? string.Empty)
            .Trim();
        if (networkId.Length == 0)
            throw new InvalidOperationException("CI start requires network_id.");

        var defaultExit = (intent.GetStringExtra("default_exit") ?? string.Empty)
            .Trim();
        if (defaultExit.Length == 0)
            throw new InvalidOperationException("CI start requires default_exit.");

        var args = new List<string> {
            "--network", networkId,
            "--state-dir", AndroidRuntimePaths.GetStateDirectory(context),
            "--socks-port", "1080",
            "--overlay-port", intent.GetIntExtra("overlay_port", 42042)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--overlay-udp-port", intent.GetIntExtra("overlay_udp_port", 42043)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--default-exit", defaultExit,
            "--egress", "direct"
        };

        var peers = intent.GetStringExtra("peers") ?? string.Empty;
        foreach (var peer in peers.Split(
                     [',', ';', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            args.Add("--peer");
            args.Add(peer);
        }

        return HostOptions.Parse(args.ToArray());
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
        string primaryOverlayAddress,
        int resetCount,
        string? resetReason)
    {
        Write(
            context,
            new {
                phase = "ready",
                node_id = state.NodeId.ToString("x10"),
                primary_overlay_address = primaryOverlayAddress,
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

    private static void Write(Context context, object content)
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
                content,
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
        File.Move(temp, path, true);
    }
}
#endif
