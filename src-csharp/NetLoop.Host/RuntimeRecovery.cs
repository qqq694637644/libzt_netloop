using System.Net.Sockets;
using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

/// <summary>
/// Bounded retries for a new business runtime. The libzt Node and identity
/// remain alive; a failed port bind or temporarily absent Managed IP must
/// never be confused with a reason to recreate the native Node.
/// </summary>
internal static class RuntimeRecovery
{
    private const int MaxAttempts = 12;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    internal static async Task<NetLoopRuntime> CreateAsync(
        LibztNode node,
        HostOptions options,
        int resetCount,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = node.RefreshNetworkState();
                if (state.ManagedAddresses.Count == 0)
                    throw new IOException("ZeroTier has not supplied a Managed IP yet.");

                var runtime = await NetLoopRuntime.CreateAsync(options, state)
                    .ConfigureAwait(false);

                if (attempt > 1)
                {
                    JsonLog.Info("runtime_creation_recovered", new {
                        attempt,
                        reset_count = resetCount,
                        route_generation = runtime.State.Generation
                    });
                }
                return runtime;
            }
            catch (Exception ex)
                when (!cancellationToken.IsCancellationRequested
                      && (ex is IOException or SocketException))
            {
                JsonLog.Error("runtime_creation_retry", new {
                    attempt,
                    max_attempts = MaxAttempts,
                    reset_count = resetCount,
                    error_type = ex.GetType().Name,
                    error = ex.Message
                });

                await ReportDegradedAsync(
                    options.StatusFile, attempt, resetCount, ex, cancellationToken)
                    .ConfigureAwait(false);

                if (attempt == MaxAttempts)
                {
                    // If the failure is permanent, allow a supervised process
                    // restart or operator intervention after this bounded
                    // recovery window, instead of spinning indefinitely.
                    throw new IOException(
                        $"NetLoop runtime unavailable after {MaxAttempts} attempts.", ex);
                }

                // 250, 500, 1000, 2000, 4000, then capped at 5000ms.
                var delayMs = Math.Min(
                    MaxRetryDelay.TotalMilliseconds,
                    250.0 * (1 << Math.Min(attempt - 1, 5)));
                await Task.Delay(
                    TimeSpan.FromMilliseconds(delayMs), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unreachable runtime recovery state.");
    }

    private static async Task ReportDegradedAsync(
        string? statusFile,
        int attempt,
        int resetCount,
        Exception error,
        CancellationToken cancellationToken)
    {
        if (statusFile is null)
            return;

        try
        {
            await StatusWriter.WriteAsync(
                statusFile,
                new {
                    phase = "degraded",
                    reset_count = resetCount,
                    attempt,
                    max_attempts = MaxAttempts,
                    reason = "runtime_creation_failed",
                    error_type = error.GetType().Name
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken status-file destination should not prevent the data
            // plane from recovering. Log this independently of its contents.
            JsonLog.Error("runtime_degraded_status_write_failed", new {
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
    }
}
