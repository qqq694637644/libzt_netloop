using System.Text.Json;
using NetLoop.Core;

namespace NetLoop.Host;

internal sealed class RecoveryCommandWatcher : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };
    private readonly string? _path;
    private readonly RecoveryCoordinator _recovery;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _loop;
    private long _lastCommandId;
    private int _disposed;

    internal RecoveryCommandWatcher(
        string? path,
        RecoveryCoordinator recovery)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
        _recovery = recovery;

        if (_path is null)
            return;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _lastCommandId = TryReadCommand(_path)?.Id ?? 0;
        _loop = WatchAsync(_stop.Token);

        JsonLog.Info("recovery_command_watcher_ready", new {
            path = _path,
            initial_command_id = _lastCommandId
        });
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var command = TryReadCommand(_path!);
                if (command is not null && command.Id > _lastCommandId)
                {
                    _lastCommandId = command.Id;
                    JsonLog.Info("recovery_command_received", new {
                        command_id = command.Id,
                        command = command.Command
                    });

                    switch (command.Command)
                    {
                        case "soft":
                            await _recovery.TriggerSoftRecoveryAsync(
                                $"command:{command.Id}",
                                command.Id,
                                cancellationToken).ConfigureAwait(false);
                            break;

                        case "hard":
                            await _recovery.TriggerHardRecoveryAsync(
                                $"command:{command.Id}",
                                command.Id,
                                cancellationToken).ConfigureAwait(false);
                            break;

                        default:
                            JsonLog.Error("recovery_command_unknown", new {
                                command_id = command.Id,
                                command = command.Command
                            });
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // Atomic file replacement can briefly race the poll. Retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above on Windows while a replace is in flight.
            }
            catch (Exception ex)
            {
                JsonLog.Error("recovery_command_failed", new {
                    error_type = ex.GetType().Name,
                    error = ex.Message
                });
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static RecoveryCommand? TryReadCommand(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<RecoveryCommand>(json, JsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private sealed record RecoveryCommand(long Id, string Command);
}
