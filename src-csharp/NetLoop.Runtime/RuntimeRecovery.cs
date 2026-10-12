using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

/// <summary>
/// Wait for missing Controller configuration and explicitly classified
/// native-network bind failures without restarting the Node. Local port
/// conflicts and all unknown errors are terminal, not blanket-retried.
/// </summary>
internal static class RuntimeRecovery
{
    internal static async Task<NetLoopRuntime> CreateAsync(
        LibztNode node,
        HostOptions options,
        int resetCount,
        Action processWaitingEvents,
        CancellationToken cancellationToken,
        bool recoveringAfterPhysicalHandover = false)
    {
        var waiting = false;
        string? lastTransientIssue = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A physical Wi-Fi/hotspot event must be processed even while
            // this owner awaits a new Managed IP or native bind readiness.
            processWaitingEvents();
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
                    var runtime = await NetLoopRuntime.CreateAsync(
                            options, state, recoveringAfterPhysicalHandover)
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
                when ((ex.IsNetworkUnavailable || ex.IsTransientRuntimeBindFailure)
                      && !cancellationToken.IsCancellationRequested)
            {
                waiting = true;
                if (ex.IsTransientRuntimeBindFailure)
                {
                    var issue = $"{ex.Operation}:{ex.SocketError}";
                    if (issue != lastTransientIssue)
                    {
                        JsonLog.Error("runtime_native_network_bind_waiting", new {
                            operation = ex.Operation,
                            api_result = ex.ApiResult,
                            socket_error = ex.SocketError,
                            reset_count = resetCount
                        });
                        lastTransientIssue = issue;
                    }
                }
            }
            // Retry only named transient network conditions. A permanent
            // local port conflict or unknown error escapes immediately.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
