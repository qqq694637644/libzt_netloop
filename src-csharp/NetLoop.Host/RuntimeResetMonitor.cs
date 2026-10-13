using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Channels;
using NetLoop.Core;

namespace NetLoop.Host;

/// <summary>
/// Windows: capture and enqueue each physical network state immediately.
/// Never cancel a real egress transition because another event arrived within
/// 250 ms. A single bounded retry loop resolves transient IP Helper failures.
/// Non-Windows keeps its established short burst coalescing policy.
/// </summary>
internal sealed class RuntimeResetMonitor : IAsyncDisposable
{
    private static readonly TimeSpan CommandPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RecaptureInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RecaptureBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OtherPlatformQuietInterval = TimeSpan.FromMilliseconds(250);

    private readonly string? _commandFile;
    private readonly Action<string, PhysicalEgressIdentity?> _requestReset;
    private readonly Func<PhysicalEgressIdentity?> _capture;
    private readonly bool _usesPlatformCapture;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<long> _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _networkGate = new();
    private readonly object _availabilityGate = new();
    private readonly Channel<string> _otherPlatformSignals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private Task? _recaptureTask;
    private Task? _otherPlatformLoop;
    private Task? _commandLoop;
    private bool _recaptureRunning;
    private bool _recapturePending;
    private long _recaptureDeadline;
    private bool _networkAvailable;
    private int _started;
    private int _disposed;

    internal RuntimeResetMonitor(
        string? commandFile,
        Action<string, PhysicalEgressIdentity?> requestReset,
        Func<PhysicalEgressIdentity?>? capture = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<long>? clock = null)
    {
        _commandFile = string.IsNullOrWhiteSpace(commandFile)
            ? null : Path.GetFullPath(commandFile);
        _requestReset = requestReset;
        _capture = capture ?? (() => PhysicalEgressIdentity.Capture());
        _usesPlatformCapture = capture is null;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _clock = clock ?? (() => Environment.TickCount64);
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Runtime reset monitor is already started.");

        if (!OperatingSystem.IsWindows())
            _otherPlatformLoop = OtherPlatformChangesAsync(_stop.Token);

        lock (_availabilityGate)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _networkAvailable = NetworkInterface.GetIsNetworkAvailable();
        }

        if (_commandFile is not null)
            _commandLoop = CommandLoopAsync(_stop.Token);

        JsonLog.Info("runtime_reset_monitor_ready", new {
            physical_egress_capture = OperatingSystem.IsWindows() ? "immediate" : "not_supported",
            capture_retry_ms = RecaptureBudget.TotalMilliseconds,
            command_file = _commandFile
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs eventArgs)
        => NotifyPhysicalNetworkChange("network_address_changed");

    private void OnNetworkAvailabilityChanged(
        object? sender, NetworkAvailabilityEventArgs eventArgs)
    {
        lock (_availabilityGate)
        {
            if (_networkAvailable == eventArgs.IsAvailable)
                return;
            _networkAvailable = eventArgs.IsAvailable;
        }

        NotifyPhysicalNetworkChange(
            eventArgs.IsAvailable ? "network_available" : "network_unavailable");
    }

    // Used directly by deterministic tests. Windows OS callbacks use this
    // same path, including immediate capture, ordering, and retry scheduling.
    internal void NotifyPhysicalNetworkChange(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!OperatingSystem.IsWindows() && _usesPlatformCapture)
        {
            // Android uses its own monitor; Linux physical callback storms are
            // still coalesced with the former 250ms policy.
            _otherPlatformSignals.Writer.TryWrite(reason);
            return;
        }

        lock (_networkGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            // Synchronous capture under one lock preserves event ordering.
            // Different egresses at <250ms can never cancel one another.
            var snapshot = _capture();
            PublishReset(reason, reason == "network_unavailable" ? null : snapshot);

            if (reason == "network_unavailable" || snapshot?.IsCaptureFailed != true)
            {
                _recapturePending = false;
                return;
            }

            // A repeated failed OS notification may not extend an already
            // active retry episode indefinitely.
            if (!_recapturePending)
                _recaptureDeadline = _clock() + (long)RecaptureBudget.TotalMilliseconds;
            _recapturePending = true;
            if (_recaptureRunning)
                return;

            _recaptureRunning = true;
            _recaptureTask = RecaptureFailedEgressAsync();
        }
    }

    private async Task RecaptureFailedEgressAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _delay(RecaptureInterval, _stop.Token).ConfigureAwait(false);
                lock (_networkGate)
                {
                    if (!_recapturePending || Volatile.Read(ref _disposed) != 0)
                        return;
                    if (_clock() >= _recaptureDeadline)
                    {
                        _recapturePending = false;
                        JsonLog.Error("physical_egress_capture_deadline", new {
                            budget_ms = RecaptureBudget.TotalMilliseconds
                        });
                        return;
                    }

                    var snapshot = _capture();
                    if (snapshot?.IsCaptureFailed == true)
                        continue;

                    _recapturePending = false;
                    // A successful recapture publishes an extra observation,
                    // without needing another Windows OS notification. Owner
                    // changes epoch only when the egress truly changed.
                    PublishReset("network_address_rechecked", snapshot);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_networkGate)
            {
                _recaptureRunning = false;
                // Another failed callback may have arrived after the prior
                // worker confirmed a route but before its finally ran.
                // Re-arm rather than leaving a pending capture orphaned.
                if (_recapturePending && Volatile.Read(ref _disposed) == 0
                    && !_stop.IsCancellationRequested)
                {
                    _recaptureRunning = true;
                    _recaptureTask = RecaptureFailedEgressAsync();
                }
            }
        }
    }

    private async Task OtherPlatformChangesAsync(CancellationToken token)
    {
        try
        {
            while (await _otherPlatformSignals.Reader.WaitToReadAsync(token)
                       .ConfigureAwait(false))
            {
                if (!_otherPlatformSignals.Reader.TryRead(out var reason))
                    continue;
                await Task.Delay(OtherPlatformQuietInterval, token)
                    .ConfigureAwait(false);
                while (_otherPlatformSignals.Reader.TryRead(out var latest))
                    reason = latest;
                PublishReset(reason, null);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task CommandLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_commandFile is not null && File.Exists(_commandFile))
                {
                    var json = await File.ReadAllTextAsync(
                        _commandFile, cancellationToken).ConfigureAwait(false);
                    var command = JsonSerializer.Deserialize<ResetCommand>(
                        json, new JsonSerializerOptions {
                            PropertyNameCaseInsensitive = true
                        });

                    if (command is not null
                        && string.Equals(command.Command, "reset",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryConsumeCommandFile(_commandFile))
                            continue;
                        PublishReset($"command:{command.Id}", null);
                    }
                }
            }
            catch (JsonException)
            {
                // The writer uses atomic replace; tolerate partial files.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(CommandPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool TryConsumeCommandFile(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void PublishReset(string reason, PhysicalEgressIdentity? egress)
    {
        JsonLog.Info("runtime_reset_requested", new { reason });
        _requestReset(reason, egress);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        await _stop.CancelAsync().ConfigureAwait(false);
        _otherPlatformSignals.Writer.TryComplete();
        if (_recaptureTask is not null)
            await _recaptureTask.ConfigureAwait(false);
        if (_otherPlatformLoop is not null)
            await _otherPlatformLoop.ConfigureAwait(false);
        if (_commandLoop is not null)
        {
            try
            {
                await _commandLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        _stop.Dispose();
    }

    private sealed record ResetCommand(long Id, string Command);
}
