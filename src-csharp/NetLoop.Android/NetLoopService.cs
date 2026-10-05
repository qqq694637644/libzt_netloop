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
    internal const string ActionStart = "com.libzt.netloop.action.START";
    internal const string ActionStop = "com.libzt.netloop.action.STOP";
#if NETLOOP_CI
    internal const string ActionCiReset = "com.libzt.netloop.ci.RESET";
#endif

    private const string ChannelId = "netloop_runtime";
    private const int NotificationId = 42042;

    private CancellationTokenSource? _stop;
    private Task? _runner;
#if NETLOOP_CI
    private Channel<string>? _ciResets;
#endif
    private int _explicitStop;

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
        if (string.Equals(
                intent?.Action,
                ActionStop,
                StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _explicitStop, 1);
            EnsureForeground("Stopping NetLoop...");
            _stop?.Cancel();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

#if NETLOOP_CI
        if (string.Equals(
                intent?.Action,
                ActionCiReset,
                StringComparison.Ordinal))
        {
            _ciResets?.Writer.TryWrite("android_ci_reset");
            return StartCommandResult.Sticky;
        }
#endif

        EnsureForeground("Starting NetLoop...");

        if (_runner is null || _runner.IsCompleted)
        {
            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            var token = _stop.Token;

            _runner = Task.Factory
                .StartNew(
                    () => RunNetLoopAsync(token),
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
            _ = ObserveRunnerAsync(_runner);
        }

        return StartCommandResult.Sticky;
    }

    private async Task RunNetLoopAsync(CancellationToken cancellationToken)
    {
        var config = AndroidConfig.Load(this);
        var options = config.RuntimeOptions;

        await using var node = new LibztNode(
            options.NetworkId,
            options.StateDirectory,
            options.StartupTimeout);
        var state = await node.StartAsync(cancellationToken)
            .ConfigureAwait(false);

        var resets = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
#if NETLOOP_CI
        _ciResets = resets;
#endif

        await using var networkMonitor = new AndroidNetworkMonitor(
            this,
            options.ResetEventDebounce,
            reason => resets.Writer.TryWrite(reason));

        NetLoopRuntime? runtime = null;
        var resetCount = 0;

        try
        {
            runtime = await NetLoopRuntime.CreateAsync(options, state)
                .ConfigureAwait(false);
            networkMonitor.Start();
#if NETLOOP_CI
            CiAutomationStatus.WriteReady(
                this,
                runtime.State,
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

                var discarded = runtime;
                runtime = null;
                discarded.Abort();
                await discarded.DisposeAsync().ConfigureAwait(false);

                node.NotifyPhysicalNetworkChanged();
                state = node.RefreshNetworkState();
                resetCount++;

                runtime = await NetLoopRuntime.CreateAsync(options, state)
                    .ConfigureAwait(false);

#if NETLOOP_CI
                CiAutomationStatus.WriteReady(
                    this,
                    runtime.State,
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
            Interlocked.Exchange(ref _explicitStop, 1);
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
        var launchIntent = new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.SingleTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent
            | PendingIntentFlags.Immutable);

        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            builder = new Notification.Builder(this, ChannelId);
        else
            builder = new Notification.Builder(this);

        return builder
            .SetContentTitle("NetLoop")
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_stat_netloop)
            .SetContentIntent(pendingIntent)
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

        if (Volatile.Read(ref _explicitStop) != 0)
        {
            // libzt documents node_free as process-final. The foreground
            // service runs in its own process so an explicit user stop can
            // terminate that process without killing the configuration UI.
            global::Android.OS.Process.KillProcess(
                global::Android.OS.Process.MyPid());
        }
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent)
        => null;
}
