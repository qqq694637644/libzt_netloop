using System.Net.NetworkInformation;
using System.Text.Json;
using NetLoop.Core;

namespace NetLoop.Host;

internal sealed class RuntimeResetMonitor : IAsyncDisposable
{
    private static readonly TimeSpan CommandPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly string? _commandFile;
    private readonly TimeSpan _debounce;
    private readonly Action<string> _requestReset;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _debounceCts;
    private Task? _commandLoop;
    private int _started;
    private int _disposed;

    internal RuntimeResetMonitor(
        string? commandFile,
        TimeSpan debounce,
        Action<string> requestReset)
    {
        _commandFile = string.IsNullOrWhiteSpace(commandFile)
            ? null
            : Path.GetFullPath(commandFile);
        _debounce = debounce;
        _requestReset = requestReset;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Runtime reset monitor is already started.");

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        if (_commandFile is not null)
            _commandLoop = CommandLoopAsync(_stop.Token);

        JsonLog.Info("runtime_reset_monitor_ready", new {
            debounce_ms = _debounce.TotalMilliseconds,
            command_file = _commandFile
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs eventArgs)
        => ScheduleNetworkReset("network_address_changed");

    private void OnNetworkAvailabilityChanged(
        object? sender,
        NetworkAvailabilityEventArgs eventArgs)
        => ScheduleNetworkReset(
            eventArgs.IsAvailable
                ? "network_available"
                : "network_unavailable");

    private void ScheduleNetworkReset(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        CancellationTokenSource current;
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            current = _debounceCts;
        }

        _ = DebounceAsync(reason, current);
    }

    private async Task DebounceAsync(
        string reason,
        CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(_debounce, debounce.Token).ConfigureAwait(false);
            RequestReset(reason);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
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
                        _commandFile,
                        cancellationToken).ConfigureAwait(false);
                    var command = JsonSerializer.Deserialize<ResetCommand>(
                        json,
                        new JsonSerializerOptions {
                            PropertyNameCaseInsensitive = true
                        });

                    if (command is not null
                        && string.Equals(
                            command.Command,
                            "reset",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryConsumeCommandFile(_commandFile))
                            continue;
                        RequestReset($"command:{command.Id}");
                    }
                }
            }
            catch (JsonException)
            {
                // The writer uses atomic replace, but tolerate a transient or
                // manually edited partial command and retry on the next poll.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(
                CommandPollInterval,
                cancellationToken).ConfigureAwait(false);
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

    private void RequestReset(string reason)
    {
        JsonLog.Info("runtime_reset_requested", new { reason });
        _requestReset(reason);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_commandLoop is not null)
        {
            try
            {
                await _commandLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private sealed record ResetCommand(long Id, string Command);
}
