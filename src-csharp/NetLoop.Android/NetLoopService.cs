using System.Threading.Channels;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using NetLoop.Core;
using NetLoop.Host;
using NetLoop.Libzt;

namespace NetLoop.Android;

[Service(
    Name = "com.libzt.netloop.NetLoopService",
    Exported = false,
    Process = ":netloop")]
public sealed class NetLoopService : Service
{
    internal const string ActionStartControlled =
        "com.libzt.netloop.action.START_CONTROLLED";
#if NETLOOP_CI
    internal const string ActionCiStart = "com.libzt.netloop.ci.START";
    internal const string ActionCiReset = "com.libzt.netloop.ci.RESET";
#endif

    private const string ChannelId = "netloop_runtime";
    private const int NotificationId = 42042;

    private CancellationTokenSource? _stop;
    private Task? _runner;
#if NETLOOP_CI
    private Channel<string>? _ciResets;
#endif
    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
#if NETLOOP_CI
        if (string.Equals(
                intent?.Action,
                ActionCiReset,
                StringComparison.Ordinal))
        {
            _ciResets?.Writer.TryWrite("android_ci_reset");
            return StartCommandResult.NotSticky;
        }
#endif

        var isControlledStart = string.Equals(
            intent?.Action,
            ActionStartControlled,
            StringComparison.Ordinal);
#if NETLOOP_CI
        var isCiStart = string.Equals(
            intent?.Action,
            ActionCiStart,
            StringComparison.Ordinal);
#else
        const bool isCiStart = false;
#endif
        if (!isControlledStart && !isCiStart)
        {
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        EnsureForeground("Starting NetLoop...");

        if (_runner is null || _runner.IsCompleted)
        {
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            var token = _stop.Token;

            HostOptions options;
            if (isControlledStart)
            {
                options = NetLoopRuntimeController
                    .GetControlledConfigForService()
                    .BuildRuntimeOptions(this);
            }
#if NETLOOP_CI
            else
            {
                options = CiAutomationOptions.FromIntent(this, intent!);
            }
#else
            else
            {
                throw new InvalidOperationException(
                    "Unsupported NetLoop service start action.");
            }
#endif
            _runner = Task.Factory
                .StartNew(
                    () => RunNetLoopAsync(options, token),
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
            _ = ObserveRunnerAsync(_runner);
        }

        return StartCommandResult.NotSticky;
    }

    private async Task RunNetLoopAsync(
        HostOptions options,
        CancellationToken cancellationToken)
    {
        NetLoopRuntimeController.ReportRuntimeStarting();

        await using var node = new LibztNode(
            options.NetworkId,
            options.StateDirectory,
            options.StartupTimeout);

        var resets = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1) {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        var resetRuntimeOnNetworkChange = 0;

        void OnNetworkChanged(string reason)
        {
            if (Volatile.Read(ref resetRuntimeOnNetworkChange) == 0)
            {
                node.TryNotifyPhysicalNetworkChanged();
                return;
            }

            resets.Writer.TryWrite(reason);
        }
#if NETLOOP_CI
        _ciResets = resets;
#endif

        await using var networkMonitor = new AndroidNetworkMonitor(
            this,
            options.ResetEventDebounce,
            OnNetworkChanged);

        NetLoopRuntime? runtime = null;
        var resetCount = 0;

        try
        {
            networkMonitor.Start();
            var state = await node.StartAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref resetRuntimeOnNetworkChange, 1);
            runtime = await NetLoopRuntime.CreateAsync(options, state)
                .ConfigureAwait(false);
            NetLoopRuntimeController.ReportReady(
                runtime.State.NodeId,
                runtime.OverlayBindAddress.ToString());
#if NETLOOP_CI
            CiAutomationStatus.WriteReady(
                this,
                runtime.State,
                runtime.OverlayBindAddress.ToString(),
                resetCount,
                null);
#endif

            while (!cancellationToken.IsCancellationRequested)
            {
                UpdateNotification(
                    $"Ready • node {runtime.State.NodeId:x10} • SOCKS 127.0.0.1:{options.SocksPort}");

                string reason;
                try
                {
                    reason = await resets.Reader
                        .ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (System.OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                JsonLog.Info("android_runtime_reset_start", new {
                    reason,
                    reset_count = resetCount
                });

                NetLoopRuntimeController.ReportRuntimeRestarting();
                Volatile.Write(ref resetRuntimeOnNetworkChange, 0);
                var discarded = runtime;
                runtime = null;
                discarded.Abort();
                await discarded.DisposeAsync().ConfigureAwait(false);

                node.NotifyPhysicalNetworkChanged();
                state = node.RefreshNetworkState();
                Volatile.Write(ref resetRuntimeOnNetworkChange, 1);
                resetCount++;

                runtime = await NetLoopRuntime.CreateAsync(options, state)
                    .ConfigureAwait(false);
                NetLoopRuntimeController.ReportReady(
                    runtime.State.NodeId,
                    runtime.OverlayBindAddress.ToString());

#if NETLOOP_CI
                CiAutomationStatus.WriteReady(
                    this,
                    runtime.State,
                    runtime.OverlayBindAddress.ToString(),
                    resetCount,
                    reason);
#endif

                JsonLog.Info("android_runtime_reset_ready", new {
                    reason,
                    reset_count = resetCount,
                    elapsed_ms = System.Diagnostics.Stopwatch
                        .GetElapsedTime(started)
                        .TotalMilliseconds
                });
            }
        }
        finally
        {
#if NETLOOP_CI
            _ciResets = null;
#endif
            if (runtime is not null)
                await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveRunnerAsync(Task runner)
    {
        try
        {
            await runner.ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            NetLoopRuntimeController.ReportStopped();
        }
        catch (Exception ex)
        {
#if NETLOOP_CI
            CiAutomationStatus.WriteError(this, ex);
#endif
            JsonLog.Error("android_service_failed", new {
                error_type = ex.GetType().FullName,
                error = ex.Message,
                stack = ex.StackTrace
            });
            UpdateNotification($"NetLoop stopped: {ex.Message}");
            StopSelf();
        }
    }

    private void EnsureForeground(string text)
    {
        var notification = BuildNotification(text);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartForeground(
                NotificationId,
                notification,
                ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void UpdateNotification(string text)
    {
        var manager = (NotificationManager?)GetSystemService(
            NotificationService);
        manager?.Notify(NotificationId, BuildNotification(text));
    }

    private Notification BuildNotification(string text)
    {
        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            builder = new Notification.Builder(this, ChannelId);
        else
            builder = new Notification.Builder(this);

        return builder
            .SetContentTitle("NetLoop")
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_stat_netloop)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .Build();
    }

    private void EnsureNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var manager = (NotificationManager?)GetSystemService(
            NotificationService);
        manager?.CreateNotificationChannel(
            new NotificationChannel(
                ChannelId,
                "NetLoop runtime",
                NotificationImportance.Low) {
                Description =
                    "Keeps the local SOCKS5 and ZeroTier peer service running."
            });
    }

    public override void OnDestroy()
    {
        _stop?.Cancel();
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();

        // libzt documents node_free as process-final. This service owns the
        // dedicated :netloop process, so once the service is destroyed there
        // is no useful managed/native state to preserve in this process.
        global::Android.OS.Process.KillProcess(
            global::Android.OS.Process.MyPid());
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent)
        => null;
}
