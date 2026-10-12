using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

/// <summary>
/// Wait for temporarily missing Controller configuration without restarting
/// the native Node. Other local programming/bind errors are terminal.
/// </summary>
internal static class RuntimeRecovery
{
    internal static async Task<NetLoopRuntime> CreateAsync(
        LibztNode node,
        HostOptions options,
        int resetCount,
        CancellationToken cancellationToken)
    {
        var waiting = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = node.RefreshNetworkState();
                if (state.ManagedAddresses.Count == 0)
                {
                    waiting = true;
                }
                else
                {
                    NetLoopRuntime.ValidateSingleManagedAddress(state.ManagedAddresses);
                    var runtime = await NetLoopRuntime.CreateAsync(options, state)
                        .ConfigureAwait(false);
                    if (waiting)
                        JsonLog.Info("runtime_controller_configuration_recovered", new {
                            reset_count = resetCount,
                            route_generation = state.Generation
                        });
                    return runtime;
                }
            }
            catch (LibztException ex)
                when (ex.IsNetworkUnavailable
                      && !cancellationToken.IsCancellationRequested)
            {
                waiting = true;
            }
            // This is not a generic retry layer: only native unavailable/empty
            // config is retryable. Do not loop forever on a port bind error.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
