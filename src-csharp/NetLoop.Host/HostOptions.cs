using System.Net;

namespace NetLoop.Host;

internal sealed class HostOptions
{
    private static readonly HashSet<string> KnownValueOptions = new(
        [
            "--network",
            "--state-dir",
            "--socks-port",
            "--overlay-port",
            "--overlay-udp-port",
            "--peer",
            "--default-exit",
            "--egress",
            "--upstream-host",
            "--upstream-port",
            "--upstream-user",
            "--upstream-password",
            "--status-file",
            "--startup-timeout",
            "--connect-timeout",
            "--udp-idle-timeout",
            "--reset-debounce-ms",
            "--reset-command-file"
        ],
        StringComparer.Ordinal);

    internal ulong NetworkId { get; init; }
    internal required string StateDirectory { get; init; }
    internal IPAddress SocksAddress => IPAddress.Loopback;
    internal ushort SocksPort { get; init; } = 1080;
    internal ushort OverlayPort { get; init; } = 42042;
    internal ushort OverlayUdpPort { get; init; } = 42043;
    internal IPAddress? DefaultExit { get; init; }
    internal IReadOnlyList<IPAddress> Peers { get; init; } = [];
    internal string Egress { get; init; } = "direct";
    internal string? UpstreamHost { get; init; }
    internal ushort UpstreamPort { get; init; } = 1080;
    internal string? UpstreamUsername { get; init; }
    internal string? UpstreamPassword { get; init; }
    internal string? StatusFile { get; init; }
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan UdpIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan ResetEventDebounce { get; init; } = TimeSpan.FromMilliseconds(250);
    internal string? ResetCommandFile { get; init; }
    internal bool ShowHelp { get; init; }
    internal bool ShowVersion { get; init; }

    internal static HostOptions Parse(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (key is "--help" or "-h" or "--version")
            {
                flags.Add(key);
                continue;
            }

            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument: {key}");
            if (!KnownValueOptions.Contains(key))
                throw new ArgumentException($"Unknown option: {key}");
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for {key}");

            if (!values.TryGetValue(key, out var list))
            {
                values[key] = list = [];
            }
            else if (key != "--peer")
            {
                throw new ArgumentException($"Duplicate option: {key}");
            }
            list.Add(args[index]);
        }

        if (flags.Contains("--help") || flags.Contains("-h"))
            return new HostOptions { StateDirectory = ".", ShowHelp = true };
        if (flags.Contains("--version"))
            return new HostOptions { StateDirectory = ".", ShowVersion = true };

        var networkText = Required(values, "--network");
        var stateDirectory = Required(values, "--state-dir");
        var networkId = Convert.ToUInt64(networkText, 16);

        var peerValues = Values(values, "--peer");
        var peers = peerValues.Select(IPAddress.Parse).Distinct().ToArray();
        var defaultExit = IPAddress.Parse(Required(values, "--default-exit"));

        var egress = Optional(values, "--egress") ?? "direct";
        if (egress is not ("direct" or "upstream-socks5"))
            throw new ArgumentException("--egress must be 'direct' or 'upstream-socks5'.");

        var upstreamHost = Optional(values, "--upstream-host");
        var upstreamUsername = Optional(values, "--upstream-user");
        var upstreamPassword = Optional(values, "--upstream-password");
        var hasUpstreamOptions = values.ContainsKey("--upstream-host")
            || values.ContainsKey("--upstream-port")
            || values.ContainsKey("--upstream-user")
            || values.ContainsKey("--upstream-password");

        if (egress == "direct" && hasUpstreamOptions)
        {
            throw new ArgumentException(
                "--upstream-* options are only valid with --egress upstream-socks5.");
        }

        if (egress == "upstream-socks5"
            && string.IsNullOrWhiteSpace(upstreamHost))
        {
            throw new ArgumentException(
                "--upstream-host is required for --egress upstream-socks5.");
        }

        if ((upstreamUsername is null) != (upstreamPassword is null))
        {
            throw new ArgumentException(
                "--upstream-user and --upstream-password must be provided together.");
        }

        return new HostOptions {
            NetworkId = networkId,
            StateDirectory = stateDirectory,
            SocksPort = ParsePort(Optional(values, "--socks-port") ?? "1080", "--socks-port"),
            OverlayPort = ParsePort(Optional(values, "--overlay-port") ?? "42042", "--overlay-port"),
            OverlayUdpPort = ParsePort(Optional(values, "--overlay-udp-port") ?? "42043", "--overlay-udp-port"),
            DefaultExit = defaultExit,
            Peers = peers,
            Egress = egress,
            UpstreamHost = upstreamHost,
            UpstreamPort = ParsePort(Optional(values, "--upstream-port") ?? "1080", "--upstream-port"),
            UpstreamUsername = upstreamUsername,
            UpstreamPassword = upstreamPassword,
            StatusFile = Optional(values, "--status-file"),
            StartupTimeout = TimeSpan.FromSeconds(ParsePositiveInt(Optional(values, "--startup-timeout") ?? "120", "--startup-timeout")),
            ConnectTimeout = TimeSpan.FromSeconds(ParsePositiveInt(Optional(values, "--connect-timeout") ?? "20", "--connect-timeout")),
            UdpIdleTimeout = TimeSpan.FromSeconds(ParsePositiveInt(Optional(values, "--udp-idle-timeout") ?? "60", "--udp-idle-timeout")),
            ResetEventDebounce = TimeSpan.FromMilliseconds(ParsePositiveInt(Optional(values, "--reset-debounce-ms") ?? "250", "--reset-debounce-ms")),
            ResetCommandFile = Optional(values, "--reset-command-file")
        };
    }

    private static string Required(Dictionary<string, List<string>> values, string key)
        => Optional(values, key) ?? throw new ArgumentException($"Missing required argument {key}.");

    private static string? Optional(Dictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var list) && list.Count != 0 ? list[^1] : null;

    private static IReadOnlyList<string> Values(Dictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var list) ? list : [];

    private static ushort ParsePort(string value, string name)
    {
        if (!ushort.TryParse(value, out var port) || port == 0)
            throw new ArgumentException($"Invalid {name}: {value}");
        return port;
    }

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"Invalid {name}: {value}");
        return parsed;
    }

    internal static string Usage =>
        """
        netloop --network <hex> --state-dir <dir> --default-exit <managed-ip> [options]

        Options:
          --socks-port <port>         Local SOCKS5 port (default 1080)
          --overlay-port <port>       NetLoop peer Agent TCP port (default 42042)
          --overlay-udp-port <port>   NetLoop peer Agent UDP port (default 42043)
          --peer <managed-ip>         Peer primary Managed IP; repeatable
          --default-exit <managed-ip> Required primary Managed IP of default egress peer
          --egress <mode>             direct | upstream-socks5 (default direct)
          --upstream-host <host>      Upstream SOCKS5 host
          --upstream-port <port>      Upstream SOCKS5 port (default 1080; upstream mode only)
          --upstream-user <user>      RFC1929 username; requires password
          --upstream-password <pass>  RFC1929 password; requires username
          --status-file <path>        Atomic readiness/status JSON output
          --startup-timeout <sec>     libzt startup timeout (default 120)
          --connect-timeout <sec>     Peer/egress connect timeout (default 20)
          --udp-idle-timeout <sec>    UDP association idle timeout (default 60)
          --reset-debounce-ms <ms>    Coalesce OS network-change events (default 250)
          --reset-command-file <path> Optional deterministic reset trigger input
          --version
          --help
        """;
}
