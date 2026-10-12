using System.Reflection;
using NetLoop.Core;
using NetLoop.Libzt;

namespace NetLoop.Host;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            var options = HostOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(HostOptions.Usage);
                return 0;
            }

            if (options.ShowVersion)
            {
                Console.WriteLine($"netloop {GetVersion()}");
                return 0;
            }

            if (options.LogFile is not null)
                JsonLog.ConfigureFile(
                    options.LogFile,
                    options.LogMaxMegabytes,
                    options.LogRetainedFiles);

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                return await RunAsync(
                    options,
                    shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (Exception ex)
        {
            JsonLog.Error("fatal", new {
                error_type = ex.GetType().FullName,
                error = ex.Message,
                stack = ex.StackTrace
            });
            return 1;
        }
        finally
        {
            using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await JsonLog.FlushAsync(flushTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A blocked output destination must not hang process shutdown.
            }
            catch (Exception)
            {
                // Logging failures must not mask the process exit code.
            }
        }
    }

    private static string GetVersion()
        => typeof(Program).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? typeof(Program).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    private static async Task<int> RunAsync(
        HostOptions options,
        CancellationToken shutdownToken)
    {
        await using var node = new LibztNode(
            options.NetworkId, options.StateDirectory, options.StartupTimeout);
        await using var owner = new RuntimeOwner(node, options);

        owner.StatusChanged += status => {
            JsonLog.Info("runtime_phase", new {
                phase = status.Phase.ToString().ToLowerInvariant(),
                reason = status.Reason,
                reset_count = status.ResetCount,
                route_generation = status.Network?.Generation,
                node = status.Network?.NodeId.ToString("x10"),
                bind_address = status.Runtime?.OverlayBindAddress.ToString(),
                tcp_session_phases = status.Runtime?.TcpSessions,
                peer_dials = status.Runtime?.PeerDials
            });
        };

        void OnMonitorEvent(string reason)
        {
            if (reason.StartsWith("network_", StringComparison.Ordinal))
                owner.RequestPhysicalNetworkRefresh(reason);
            else
                owner.RequestManualReset(reason);
        }

        await using var resetMonitor = new RuntimeResetMonitor(
            options.ResetCommandFile,
            options.ResetEventDebounce,
            OnMonitorEvent);
        resetMonitor.Start();

        await owner.RunAsync(shutdownToken).ConfigureAwait(false);
        return 0;
    }
}
