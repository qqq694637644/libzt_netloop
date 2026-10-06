using System.Globalization;
using System.Net;
using System.Text.Json;
using Android.Content;
using NetLoop.Host;
using NetLoop.Socks;

namespace NetLoop.Android;

internal sealed record AndroidConfig(
    HostOptions RuntimeOptions)
{
    private const string ConfigFileName = "netloop-config.json";

    internal sealed record Snapshot(
        string NetworkId,
        string Peers,
        string DefaultExit,
        string OverlayPort,
        string OverlayUdpPort,
        string UpstreamHost,
        string UpstreamPort,
        string UpstreamUser,
        string UpstreamPassword)
    {
        internal static Snapshot Empty { get; } = new(
            string.Empty,
            string.Empty,
            string.Empty,
            "42042",
            "42043",
            string.Empty,
            "1080",
            string.Empty,
            string.Empty);
    }

    internal static AndroidConfig Load(Context context)
    {
        var config = ReadSnapshot(context)
            ?? throw new InvalidOperationException(
                "NetLoop configuration is missing. Use Save & Start first.");

        var networkText = config.NetworkId.Trim();
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
        var defaultExitText = config.DefaultExit.Trim();
        if (defaultExitText.Length == 0)
        {
            throw new InvalidOperationException(
                "Configure the default exit Managed IP before starting NetLoop.");
        }
        var defaultExit = IPAddress.Parse(defaultExitText);
        var overlayPort = ParsePort(
            config.OverlayPort,
            "overlay TCP",
            42042);
        var overlayUdpPort = ParsePort(
            config.OverlayUdpPort,
            "overlay UDP",
            42043);

        var upstreamHost = EmptyToNull(config.UpstreamHost);
        var upstreamUsername = EmptyToNull(config.UpstreamUser);
        var upstreamPassword = EmptyToNull(config.UpstreamPassword);
        if (upstreamHost is null
            && (upstreamUsername is not null || upstreamPassword is not null))
        {
            throw new InvalidOperationException(
                "Upstream SOCKS5 credentials require an upstream host.");
        }
        Socks5Credentials.Validate(upstreamUsername, upstreamPassword);
        var upstreamPortText = config.UpstreamPort.Trim();
        if (!ushort.TryParse(upstreamPortText, out var upstreamPort)
            || upstreamPort == 0)
        {
            throw new InvalidOperationException(
                $"Invalid upstream SOCKS5 port: {upstreamPortText}");
        }

        var stateDirectory = Path.Combine(
            context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException(
                "Android FilesDir is unavailable."),
            "netloop-state");

        var options = new HostOptions {
            NetworkId = networkId,
            StateDirectory = stateDirectory,
            SocksPort = 1080,
            OverlayPort = overlayPort,
            OverlayUdpPort = overlayUdpPort,
            DefaultExit = defaultExit,
            Peers = peers,
            Egress = upstreamHost is null
                ? "direct"
                : "upstream-socks5",
            UpstreamHost = upstreamHost,
            UpstreamPort = upstreamPort,
            UpstreamUsername = upstreamUsername,
            UpstreamPassword = upstreamPassword,
            StartupTimeout = TimeSpan.FromSeconds(120),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UdpIdleTimeout = TimeSpan.FromSeconds(60),
            ResetEventDebounce = TimeSpan.FromMilliseconds(250)
        };

        return new AndroidConfig(options);
    }

    internal static Snapshot LoadSnapshotOrDefault(Context context)
        => ReadSnapshot(context) ?? Snapshot.Empty;

    internal static void SaveSnapshot(
        Context context,
        Snapshot snapshot)
    {
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

    private static Snapshot? ReadSnapshot(Context context)
    {
        var path = GetConfigPath(context);
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<Snapshot>(
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

    private static IReadOnlyList<IPAddress> ParsePeers(string text)
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

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static ushort ParsePort(
        string value,
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
}
