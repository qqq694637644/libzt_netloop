using System.Globalization;
using System.Net;
using Android.Content;
using NetLoop.Host;

namespace NetLoop.Android;

internal sealed record AndroidConfig(
    HostOptions RuntimeOptions,
    string NetworkIdText)
{
    internal const string PreferencesName = "netloop";
    internal const string NetworkIdKey = "network_id";
    internal const string PeersKey = "peers";
    internal const string DefaultExitKey = "default_exit";
    internal const string OverlayPortKey = "overlay_port";
    internal const string OverlayUdpPortKey = "overlay_udp_port";
    internal const string UpstreamHostKey = "upstream_host";
    internal const string UpstreamPortKey = "upstream_port";
    internal const string UpstreamUserKey = "upstream_user";
    internal const string UpstreamPasswordKey = "upstream_password";

    internal static AndroidConfig Load(Context context)
    {
        var preferences = context.GetSharedPreferences(
            PreferencesName,
            FileCreationMode.Private)
            ?? throw new InvalidOperationException("Unable to open NetLoop preferences.");

        var networkText = (preferences.GetString(NetworkIdKey, null) ?? string.Empty)
            .Trim();
        if (networkText.Length == 0
            || !ulong.TryParse(
                networkText,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var networkId))
        {
            throw new InvalidOperationException(
                "Configure a valid hexadecimal ZeroTier network ID before starting NetLoop.");
        }

        var peers = ParsePeers(preferences.GetString(PeersKey, string.Empty));
        var defaultExitText = (preferences.GetString(DefaultExitKey, string.Empty)
            ?? string.Empty).Trim();
        var defaultExit = defaultExitText.Length == 0
            ? null
            : IPAddress.Parse(defaultExitText);
        var overlayPort = ParsePort(
            preferences.GetString(OverlayPortKey, "42042"),
            "overlay TCP",
            42042);
        var overlayUdpPort = ParsePort(
            preferences.GetString(OverlayUdpPortKey, "42043"),
            "overlay UDP",
            42043);

        var upstreamHost = (preferences.GetString(UpstreamHostKey, string.Empty)
            ?? string.Empty).Trim();
        var upstreamPortText = (preferences.GetString(UpstreamPortKey, "1080")
            ?? "1080").Trim();
        if (!ushort.TryParse(upstreamPortText, out var upstreamPort)
            || upstreamPort == 0)
        {
            throw new InvalidOperationException(
                $"Invalid upstream SOCKS5 port: {upstreamPortText}");
        }

        var stateDirectory = Path.Combine(
            context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android FilesDir is unavailable."),
            "netloop-state");

        var options = new HostOptions {
            NetworkId = networkId,
            StateDirectory = stateDirectory,
            SocksAddress = IPAddress.Loopback,
            SocksPort = 1080,
            OverlayPort = overlayPort,
            OverlayUdpPort = overlayUdpPort,
            DefaultExit = defaultExit,
            Peers = peers,
            Egress = upstreamHost.Length == 0
                ? "direct"
                : "upstream-socks5",
            UpstreamHost = upstreamHost.Length == 0 ? null : upstreamHost,
            UpstreamPort = upstreamPort,
            UpstreamUsername = EmptyToNull(
                preferences.GetString(UpstreamUserKey, null)),
            UpstreamPassword = EmptyToNull(
                preferences.GetString(UpstreamPasswordKey, null)),
            StartupTimeout = TimeSpan.FromSeconds(120),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UdpIdleTimeout = TimeSpan.FromSeconds(60),
            ResetEventDebounce = TimeSpan.FromMilliseconds(250)
        };

        return new AndroidConfig(options, networkText);
    }

    private static IReadOnlyList<IPAddress> ParsePeers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split(
                [',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Select(IPAddress.Parse)
            .Distinct()
            .ToArray();
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParsePort(
        string? value,
        string label,
        int defaultValue)
    {
        var text = string.IsNullOrWhiteSpace(value)
            ? defaultValue.ToString(CultureInfo.InvariantCulture)
            : value.Trim();
        if (!ushort.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port == 0)
        {
            throw new InvalidOperationException(
                $"Invalid {label} port: {text}");
        }
        return port;
    }
}
