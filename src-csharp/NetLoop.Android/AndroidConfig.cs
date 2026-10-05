using System.Globalization;
using System.Net;
using System.Text.Json;
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
    private const string ConfigFileName = "netloop-config.json";

    internal static AndroidConfig Load(Context context)
    {
        var config = ReadSnapshot(context);

        var networkText = (config.NetworkId ?? string.Empty).Trim();
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

        var peers = ParsePeers(config.Peers);
        var defaultExitText = (config.DefaultExit ?? string.Empty).Trim();
        var defaultExit = defaultExitText.Length == 0
            ? null
            : IPAddress.Parse(defaultExitText);
        var overlayPort = ParsePort(
            config.OverlayPort,
            "overlay TCP",
            42042);
        var overlayUdpPort = ParsePort(
            config.OverlayUdpPort,
            "overlay UDP",
            42043);

        var upstreamHost = (config.UpstreamHost ?? string.Empty).Trim();
        var upstreamPortText = (config.UpstreamPort ?? "1080").Trim();
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
                config.UpstreamUser),
            UpstreamPassword = EmptyToNull(
                config.UpstreamPassword),
            StartupTimeout = TimeSpan.FromSeconds(120),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UdpIdleTimeout = TimeSpan.FromSeconds(60),
            ResetEventDebounce = TimeSpan.FromMilliseconds(250)
        };

        return new AndroidConfig(options, networkText);
    }

    internal static void SaveSnapshot(
        Context context,
        ISharedPreferences preferences)
    {
        var snapshot = new PersistedConfig {
            NetworkId = preferences.GetString(NetworkIdKey, string.Empty),
            Peers = preferences.GetString(PeersKey, string.Empty),
            DefaultExit = preferences.GetString(DefaultExitKey, string.Empty),
            OverlayPort = preferences.GetString(OverlayPortKey, "42042"),
            OverlayUdpPort = preferences.GetString(
                OverlayUdpPortKey,
                "42043"),
            UpstreamHost = preferences.GetString(
                UpstreamHostKey,
                string.Empty),
            UpstreamPort = preferences.GetString(UpstreamPortKey, "1080"),
            UpstreamUser = preferences.GetString(
                UpstreamUserKey,
                string.Empty),
            UpstreamPassword = preferences.GetString(
                UpstreamPasswordKey,
                string.Empty)
        };

        var path = GetConfigPath(context);
        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
        File.Move(temp, path, true);
    }

    private static PersistedConfig ReadSnapshot(Context context)
    {
        var path = GetConfigPath(context);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "NetLoop configuration is missing. Use Save & Start first.");
        }

        return JsonSerializer.Deserialize<PersistedConfig>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions {
                       PropertyNameCaseInsensitive = true
                   })
               ?? throw new InvalidOperationException(
                   "NetLoop configuration file is empty.");
    }

    private static string GetConfigPath(Context context)
    {
        var directory = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException(
                "Android FilesDir is unavailable.");
        return Path.Combine(directory, ConfigFileName);
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

    private static ushort ParsePort(
        string? value,
        string label,
        ushort defaultValue)
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

    private sealed class PersistedConfig
    {
        public string? NetworkId { get; init; }
        public string? Peers { get; init; }
        public string? DefaultExit { get; init; }
        public string? OverlayPort { get; init; }
        public string? OverlayUdpPort { get; init; }
        public string? UpstreamHost { get; init; }
        public string? UpstreamPort { get; init; }
        public string? UpstreamUser { get; init; }
        public string? UpstreamPassword { get; init; }
    }
}
