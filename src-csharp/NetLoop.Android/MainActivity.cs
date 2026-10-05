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

        var preferences = GetSharedPreferences(
            AndroidConfig.PreferencesName,
            FileCreationMode.Private)
            ?? throw new InvalidOperationException(
                "Unable to open NetLoop preferences.");

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
            preferences.GetString(AndroidConfig.NetworkIdKey, string.Empty));
        _peers = AddField(
            root,
            "Peer Managed IPs (comma/space separated)",
            preferences.GetString(AndroidConfig.PeersKey, string.Empty));
        _defaultExit = AddField(
            root,
            "Default exit Managed IP (optional)",
            preferences.GetString(AndroidConfig.DefaultExitKey, string.Empty));
        _upstreamHost = AddField(
            root,
            "Upstream SOCKS5 host (optional)",
            preferences.GetString(AndroidConfig.UpstreamHostKey, string.Empty));
        _upstreamPort = AddField(
            root,
            "Upstream SOCKS5 port",
            preferences.GetString(AndroidConfig.UpstreamPortKey, "1080"));
        _upstreamUser = AddField(
            root,
            "Upstream SOCKS5 username (optional)",
            preferences.GetString(AndroidConfig.UpstreamUserKey, string.Empty));
        _upstreamPassword = AddField(
            root,
            "Upstream SOCKS5 password (optional)",
            preferences.GetString(AndroidConfig.UpstreamPasswordKey, string.Empty));
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

        var preferences = GetSharedPreferences(
            AndroidConfig.PreferencesName,
            FileCreationMode.Private)
            ?? throw new InvalidOperationException(
                "Unable to open NetLoop CI preferences.");
        var editor = preferences.Edit()
            ?? throw new InvalidOperationException(
                "Unable to edit NetLoop CI preferences.");

        editor.PutString(AndroidConfig.NetworkIdKey, networkId);
        editor.PutString(
            AndroidConfig.PeersKey,
            intent.GetStringExtra("peers") ?? string.Empty);
        editor.PutString(
            AndroidConfig.DefaultExitKey,
            intent.GetStringExtra("default_exit") ?? string.Empty);
        editor.PutString(
            AndroidConfig.OverlayPortKey,
            intent.GetIntExtra("overlay_port", 42042)
                .ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        editor.PutString(
            AndroidConfig.OverlayUdpPortKey,
            intent.GetIntExtra("overlay_udp_port", 42043)
                .ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        editor.PutString(AndroidConfig.UpstreamHostKey, string.Empty);
        editor.PutString(AndroidConfig.UpstreamUserKey, string.Empty);
        editor.PutString(AndroidConfig.UpstreamPasswordKey, string.Empty);
        if (!editor.Commit())
            throw new IOException("Unable to persist NetLoop CI preferences.");

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

        var preferences = GetSharedPreferences(
            AndroidConfig.PreferencesName,
            FileCreationMode.Private)
            ?? throw new InvalidOperationException(
                "Unable to open NetLoop preferences.");
        var editor = preferences.Edit()
            ?? throw new InvalidOperationException(
                "Unable to edit NetLoop preferences.");

        editor.PutString(AndroidConfig.NetworkIdKey, networkId);
        editor.PutString(
            AndroidConfig.PeersKey,
            _peers?.Text ?? string.Empty);
        editor.PutString(
            AndroidConfig.DefaultExitKey,
            _defaultExit?.Text ?? string.Empty);
        editor.PutString(
            AndroidConfig.UpstreamHostKey,
            _upstreamHost?.Text ?? string.Empty);
        editor.PutString(
            AndroidConfig.UpstreamPortKey,
            _upstreamPort?.Text ?? "1080");
        editor.PutString(
            AndroidConfig.UpstreamUserKey,
            _upstreamUser?.Text ?? string.Empty);
        editor.PutString(
            AndroidConfig.UpstreamPasswordKey,
            _upstreamPassword?.Text ?? string.Empty);
        if (!editor.Commit())
            throw new IOException("Unable to persist NetLoop preferences.");

        // SharedPreferences remains a UI convenience only. The foreground
        // service runs in :netloop, so cross-process configuration is carried
        // by an atomically replaced app-private JSON snapshot instead.
        AndroidConfig.SaveSnapshot(this, preferences);

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
        var stopIntent = new Intent(this, typeof(NetLoopService))
            .SetAction(NetLoopService.ActionStop);
        StartServiceCompat(stopIntent);
        _ = StartAfterStopAsync();
    }

    private async Task StartAfterStopAsync()
    {
        try
        {
            // The runtime lives in a dedicated process. Give the explicit stop
            // enough time to destroy that process before starting a new one so
            // the new service always reads the just-written config snapshot.
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            var startIntent = new Intent(this, typeof(NetLoopService))
                .SetAction(NetLoopService.ActionStart);
            StartServiceCompat(startIntent);
        }
        catch (Exception ex)
        {
            if (_status is not null)
                _status.Text = $"Unable to restart NetLoop: {ex.Message}";
        }
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
        var intent = new Intent(this, typeof(NetLoopService))
            .SetAction(NetLoopService.ActionStop);

        StartServiceCompat(intent);

        if (_status is not null)
            _status.Text = "NetLoop foreground service stop requested.";
    }

    private int Dp(int value)
        => checked((int)(value * Resources?.DisplayMetrics?.Density ?? value));
}
