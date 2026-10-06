using System.Globalization;
using System.Net;
using System.Text.Json;
using Android.Content;
using NetLoop.Host;

namespace NetLoop.Android;

internal sealed class ControlledRuntimeConfig
{
    private const string ConfigFileName = "netloop-runtime-config.json";

    private ControlledRuntimeConfig(
        ulong networkId,
        IPAddress defaultExit,
        IReadOnlyList<IPAddress> peers)
    {
        NetworkId = networkId;
        DefaultExit = defaultExit;
        Peers = peers;
    }

    internal ulong NetworkId { get; }

    internal IPAddress DefaultExit { get; }

    internal IReadOnlyList<IPAddress> Peers { get; }

    internal static ControlledRuntimeConfig Parse(
        string networkIdText,
        string defaultExitText,
        IEnumerable<string> peerTexts)
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

        var peers = new HashSet<IPAddress>();
        foreach (var value in peerTexts)
        {
            var text = value.Trim();
            if (text.Length == 0)
                continue;

            if (!IPAddress.TryParse(text, out var peer))
            {
                throw new InvalidOperationException(
                    $"Invalid NetLoop direct peer Managed IP: {text}");
            }

            if (peer.Equals(defaultExit))
            {
                throw new InvalidOperationException(
                    "NetLoop direct peers must not repeat default_exit.");
            }

            peers.Add(peer);
        }

        return new ControlledRuntimeConfig(
            networkId,
            defaultExit,
            peers
                .OrderBy(AddressSortKey, StringComparer.Ordinal)
                .ToArray());
    }

    internal static ControlledRuntimeConfig Load(Context context)
        => TryLoad(context)
           ?? throw new InvalidOperationException(
               "Controlled NetLoop runtime configuration is missing.");

    internal static ControlledRuntimeConfig? TryLoad(Context context)
    {
        var path = GetConfigPath(context);
        if (!File.Exists(path))
            return null;

        var snapshot = JsonSerializer.Deserialize<Snapshot>(
                           File.ReadAllText(path),
                           new JsonSerializerOptions {
                               PropertyNameCaseInsensitive = true
                           })
                       ?? throw new InvalidOperationException(
                           "Controlled NetLoop runtime configuration is empty.");

        return Parse(
            snapshot.NetworkId,
            snapshot.DefaultExit,
            snapshot.Peers ?? []);
    }

    internal void Save(Context context)
    {
        var snapshot = new Snapshot(
            NetworkId.ToString("x16", CultureInfo.InvariantCulture),
            DefaultExit.ToString(),
            Peers.Select(static peer => peer.ToString()).ToArray());

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

    internal static void Clear(Context context)
    {
        var path = GetConfigPath(context);
        if (File.Exists(path))
            File.Delete(path);
    }

    internal HostOptions BuildRuntimeOptions(Context context)
        => new() {
            NetworkId = NetworkId,
            StateDirectory = AndroidConfig.GetStateDirectory(context),
            SocksPort = 1080,
            OverlayPort = 42042,
            OverlayUdpPort = 42043,
            DefaultExit = DefaultExit,
            Peers = Peers,
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
    {
        if (NetworkId != other.NetworkId
            || !DefaultExit.Equals(other.DefaultExit)
            || Peers.Count != other.Peers.Count)
        {
            return false;
        }

        return Peers.SequenceEqual(other.Peers);
    }

    private static string GetConfigPath(Context context)
    {
        var directory = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException(
                "Android FilesDir is unavailable.");
        return Path.Combine(directory, ConfigFileName);
    }

    private static string AddressSortKey(IPAddress address)
        => $"{(int)address.AddressFamily:D4}:{Convert.ToHexString(address.GetAddressBytes())}";

    private sealed record Snapshot(
        string NetworkId,
        string DefaultExit,
        string[]? Peers);
}
