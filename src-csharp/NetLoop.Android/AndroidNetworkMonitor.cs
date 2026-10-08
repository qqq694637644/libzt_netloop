using Android.Content;
using Android.Net;
using NetLoop.Core;

namespace NetLoop.Android;

internal sealed class AndroidNetworkMonitor : ConnectivityManager.NetworkCallback, IAsyncDisposable
{
    private readonly ConnectivityManager _manager;
    private readonly TimeSpan _debounce;
    private readonly Action<string> _onNetworkChanged;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private string? _defaultNetwork;
    private string? _linkPropertiesFingerprint;
    private int _started;
    private int _disposed;

    internal AndroidNetworkMonitor(
        Context context,
        TimeSpan debounce,
        Action<string> onNetworkChanged)
    {
        _manager = (ConnectivityManager?)context.GetSystemService(
            Context.ConnectivityService)
            ?? throw new InvalidOperationException(
                "Android ConnectivityManager is unavailable.");
        _debounce = debounce;
        _onNetworkChanged = onNetworkChanged;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException(
                "Android network monitor is already started.");

        var activeNetwork = _manager.ActiveNetwork;
        var activeProperties = activeNetwork is null
            ? null
            : _manager.GetLinkProperties(activeNetwork);
        lock (_gate)
        {
            _defaultNetwork = activeNetwork?.ToString();
            _linkPropertiesFingerprint = activeProperties is null
                ? null
                : BuildLinkPropertiesFingerprint(activeProperties);
        }

        _manager.RegisterDefaultNetworkCallback(this);
        JsonLog.Info("android_network_monitor_ready", new {
            debounce_ms = _debounce.TotalMilliseconds,
            default_network = _defaultNetwork
        });
    }

    public override void OnAvailable(Network network)
    {
        var identity = network.ToString();
        var shouldReset = false;

        lock (_gate)
        {
            if (!string.Equals(
                    _defaultNetwork,
                    identity,
                    StringComparison.Ordinal))
            {
                _defaultNetwork = identity;
                _linkPropertiesFingerprint = null;
                shouldReset = true;
            }
        }

        if (shouldReset)
            Schedule("android_default_network_changed");
    }

    public override void OnLost(Network network)
    {
        var identity = network.ToString();
        var shouldReset = false;

        lock (_gate)
        {
            if (string.Equals(
                    _defaultNetwork,
                    identity,
                    StringComparison.Ordinal))
            {
                _defaultNetwork = null;
                _linkPropertiesFingerprint = null;
                shouldReset = true;
            }
        }

        if (shouldReset)
            Schedule("android_default_network_lost");
    }

    public override void OnLinkPropertiesChanged(
        Network network,
        LinkProperties linkProperties)
    {
        var identity = network.ToString();
        var fingerprint = BuildLinkPropertiesFingerprint(linkProperties);
        var shouldReset = false;

        lock (_gate)
        {
            if (!string.Equals(
                    _defaultNetwork,
                    identity,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (_linkPropertiesFingerprint is null)
            {
                _linkPropertiesFingerprint = fingerprint;
            }
            else if (!string.Equals(
                         _linkPropertiesFingerprint,
                         fingerprint,
                         StringComparison.Ordinal))
            {
                _linkPropertiesFingerprint = fingerprint;
                shouldReset = true;
            }
        }

        if (shouldReset)
            Schedule("android_link_properties_changed");
    }

    private static string BuildLinkPropertiesFingerprint(
        LinkProperties properties)
    {
        var addresses = properties.LinkAddresses
            .Select(static value => value.ToString())
            .OrderBy(static value => value, StringComparer.Ordinal);
        var routes = properties.Routes
            .Select(static value => value.ToString())
            .OrderBy(static value => value, StringComparer.Ordinal);

        return string.Join(",", addresses)
               + "|"
               + string.Join(",", routes);
    }

    private void Schedule(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        CancellationTokenSource debounce;
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts =
                CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            debounce = _debounceCts;
        }

        _ = DebounceAsync(reason, debounce.Token);
    }

    private async Task DebounceAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounce, cancellationToken)
                .ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
                _onNetworkChanged(reason);
        }
        catch (System.OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        try
        {
            _manager.UnregisterNetworkCallback(this);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
        }

        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        _stop.Cancel();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
