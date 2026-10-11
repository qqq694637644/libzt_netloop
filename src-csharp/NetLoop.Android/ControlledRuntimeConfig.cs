using System.Globalization;
using System.Net;
using Android.Content;
using NetLoop.Host;

namespace NetLoop.Android;

internal sealed class ControlledRuntimeConfig
{
    private ControlledRuntimeConfig(
        ulong networkId,
        IPAddress defaultExit)
    {
        NetworkId = networkId;
        DefaultExit = defaultExit;
    }

    internal ulong NetworkId { get; }

    internal IPAddress DefaultExit { get; }

    internal static ControlledRuntimeConfig Parse(
        string networkIdText,
        string defaultExitText)
    {
        var networkText = networkIdText.Trim();
        if (networkText.Length == 0
            || !ulong.TryParse(
                networkText,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var networkId))
        {
            throw new InvalidOperationException(
                "Invalid NetLoop network ID.");
        }

        if (!IPAddress.TryParse(defaultExitText.Trim(), out var defaultExit))
        {
            throw new InvalidOperationException(
                "Invalid NetLoop default exit Managed IP.");
        }

        return new ControlledRuntimeConfig(networkId, defaultExit);
    }

    internal HostOptions BuildRuntimeOptions(Context context)
        => new() {
            NetworkId = NetworkId,
            StateDirectory = AndroidRuntimePaths.GetStateDirectory(context),
            SocksPort = 1080,
            OverlayPort = 42042,
            OverlayUdpPort = 42043,
            DefaultExit = DefaultExit,
            Egress = "direct",
            UpstreamHost = null,
            UpstreamPort = 1080,
            UpstreamUsername = null,
            UpstreamPassword = null,
            StartupTimeout = TimeSpan.FromSeconds(120),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UdpIdleTimeout = TimeSpan.FromSeconds(60),
            ResetEventDebounce = TimeSpan.FromMilliseconds(250)
        };

    internal bool IsEquivalentTo(ControlledRuntimeConfig other)
        => NetworkId == other.NetworkId
           && DefaultExit.Equals(other.DefaultExit);
}
