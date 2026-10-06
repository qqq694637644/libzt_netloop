using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace NetLoop.Android;

[Activity(
    Name = "com.libzt.netloop.MainActivity",
    Label = "NetLoop",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    private EditText? _networkId;
    private EditText? _peers;
    private EditText? _defaultExit;
    private EditText? _upstreamHost;
    private EditText? _upstreamPort;
    private EditText? _upstreamUser;
    private EditText? _upstreamPassword;
    private TextView? _status;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

#if NETLOOP_CI
        ApplyCiConfigurationFromIntent();
#endif

        var config = AndroidConfig.LoadSnapshotOrDefault(this);

        var root = new LinearLayout(this) {
            Orientation = Orientation.Vertical
        };
        var padding = Dp(20);
        root.SetPadding(padding, padding, padding, padding);

        var title = new TextView(this) {
            Text = "NetLoop",
            TextSize = 24
        };
        root.AddView(title);

        _networkId = AddField(
            root,
            "ZeroTier network ID (hex)",
            config.NetworkId);
        _defaultExit = AddField(
            root,
            "Default exit Managed IP (required)",
            config.DefaultExit);
        _peers = AddField(
            root,
            "Direct peers (optional; empty = self + default exit only)",
            config.Peers);
        _upstreamHost = AddField(
            root,
            "Upstream SOCKS5 host (optional)",
            config.UpstreamHost);
        _upstreamPort = AddField(
            root,
            "Upstream SOCKS5 port",
            config.UpstreamPort);
        _upstreamUser = AddField(
            root,
            "Upstream SOCKS5 username (optional)",
            config.UpstreamUser);
        _upstreamPassword = AddField(
            root,
            "Upstream SOCKS5 password (optional)",
            config.UpstreamPassword);
        _upstreamPassword.InputType =
            global::Android.Text.InputTypes.ClassText
            | global::Android.Text.InputTypes.TextVariationPassword;

        var buttons = new LinearLayout(this) {
            Orientation = Orientation.Horizontal
        };

        var start = new Button(this) { Text = "Save & Start" };
        start.Click += (_, _) => StartNetLoop();
        buttons.AddView(
            start,
            new LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.WrapContent,
                1));

        var stop = new Button(this) { Text = "Stop" };
        stop.Click += (_, _) => StopNetLoop();
        buttons.AddView(
            stop,
            new LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.WrapContent,
                1));

        root.AddView(buttons);

        _status = new TextView(this) {
            Text = "SOCKS5 listens on 127.0.0.1:1080 while the foreground service is running."
        };
        root.AddView(_status);

        var scroll = new ScrollView(this);
        scroll.AddView(root);
        SetContentView(scroll);

#if NETLOOP_CI
        if (Intent?.GetBooleanExtra("netloop_ci_start", false) == true)
            StartNetLoop();
#endif
    }

    private EditText AddField(
        LinearLayout root,
        string hint,
        string? value)
    {
        var field = new EditText(this) {
            Hint = hint,
            Text = value ?? string.Empty
        };
        field.SetSingleLine(true);
        root.AddView(
            field,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent));
        return field;
    }

#if NETLOOP_CI
    private void ApplyCiConfigurationFromIntent()
    {
        var intent = Intent;
        if (intent?.GetBooleanExtra("netloop_ci_start", false) != true)
            return;

        var networkId = (intent.GetStringExtra("network_id") ?? string.Empty).Trim();
        if (networkId.Length == 0)
            throw new InvalidOperationException("CI start requires network_id.");

        AndroidConfig.SaveSnapshot(
            this,
            new AndroidConfig.Snapshot(
                networkId,
                intent.GetStringExtra("peers") ?? string.Empty,
                intent.GetStringExtra("default_exit") ?? string.Empty,
                intent.GetIntExtra("overlay_port", 42042)
                    .ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                intent.GetIntExtra("overlay_udp_port", 42043)
                    .ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                string.Empty,
                "1080",
                string.Empty,
                string.Empty));

        CiAutomationStatus.Delete(this);
    }
#endif

    private void StartNetLoop()
    {
        var networkId = _networkId?.Text?.Trim() ?? string.Empty;
        if (networkId.Length == 0
            || !ulong.TryParse(
                networkId,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            Toast.MakeText(
                    this,
                    "Enter a valid hexadecimal ZeroTier network ID.",
                    ToastLength.Long)
                ?.Show();
            return;
        }

        var current = AndroidConfig.LoadSnapshotOrDefault(this);
        AndroidConfig.SaveSnapshot(
            this,
            new AndroidConfig.Snapshot(
                networkId,
                _peers?.Text ?? string.Empty,
                _defaultExit?.Text ?? string.Empty,
                current.OverlayPort,
                current.OverlayUdpPort,
                _upstreamHost?.Text ?? string.Empty,
                _upstreamPort?.Text ?? "1080",
                _upstreamUser?.Text ?? string.Empty,
                _upstreamPassword?.Text ?? string.Empty));

        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && CheckSelfPermission(Manifest.Permission.PostNotifications)
               != Permission.Granted)
        {
            RequestPermissions(
                [Manifest.Permission.PostNotifications],
                1001);
        }

        RestartNetLoopService();

        if (_status is not null)
            _status.Text = "NetLoop foreground service restart requested.";
    }

    private void RestartNetLoopService()
    {
        var serviceIntent = new Intent(this, typeof(NetLoopService));
        var wasRunning = StopService(serviceIntent);
        if (!wasRunning)
        {
            StartNetLoopService();
            return;
        }

        _ = StartAfterProcessExitAsync();
    }

    private async Task StartAfterProcessExitAsync()
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (IsNetLoopProcessRunning())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        "Timed out waiting for old NetLoop service process to exit.");
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(50)).ConfigureAwait(true);
            }

            StartNetLoopService();
        }
        catch (Exception ex)
        {
            if (_status is not null)
                _status.Text = $"Unable to restart NetLoop: {ex.Message}";
        }
    }

    private void StartNetLoopService()
    {
        var startIntent = new Intent(this, typeof(NetLoopService))
            .SetAction(NetLoopService.ActionStart);
        StartServiceCompat(startIntent);
    }

    private bool IsNetLoopProcessRunning()
    {
        var manager = GetSystemService(ActivityService) as ActivityManager;
        var processName = $"{PackageName}:netloop";
        return manager?.RunningAppProcesses?.Any(
                   process => string.Equals(
                       process.ProcessName,
                       processName,
                       StringComparison.Ordinal)) == true;
    }

    private void StartServiceCompat(Intent intent)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            StartForegroundService(intent);
        else
            StartService(intent);
    }

    private void StopNetLoop()
    {
        StopService(new Intent(this, typeof(NetLoopService)));

        if (_status is not null)
            _status.Text = "NetLoop foreground service stop requested.";
    }

    private int Dp(int value)
        => checked((int)(value * Resources?.DisplayMetrics?.Density ?? value));
}
