using System.Net;
using NetLoop.Core;
using NetLoop.Socks;

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
            "--agent-allow-source",
            "--agent-allow-target-port",
            "--default-exit",
            "--egress",
            "--upstream-host",
            "--upstream-port",
            "--upstream-user",
            "--upstream-password",
            "--log-file",
            "--log-max-mb",
            "--log-retain",
            "--startup-timeout",
            "--connect-timeout",
            "--socks-handshake-timeout",
            "--socks-connect-reply-timeout",
            "--max-tcp-tunnels",
            "--max-udp-associations",
            "--relay-buffer-kib",
            "--udp-queue-capacity",
            "--udp-send-timeout",
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
    internal IReadOnlyList<OverlayPrefix> AgentAllowedSources { get; init; } = [];
    internal IReadOnlyList<AgentAccessPolicy.PortRange> AgentAllowedTargetPorts { get; init; } = [];
    internal IPAddress? DefaultExit { get; init; }
    internal string Egress { get; init; } = "direct";
    internal string? UpstreamHost { get; init; }
    internal ushort UpstreamPort { get; init; } = 1080;
    internal string? UpstreamUsername { get; init; }
    internal string? UpstreamPassword { get; init; }
    internal string? LogFile { get; init; }
    internal int LogMaxMegabytes { get; init; } = 16;
    internal int LogRetainedFiles { get; init; } = 5;
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal TimeSpan SocksHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan SocksConnectReplyTimeout { get; init; } = TimeSpan.FromSeconds(25);
    internal TimeSpan UdpIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal int MaxTcpTunnels { get; init; } = 128;
    internal int MaxUdpAssociations { get; init; } = 64;
    internal int RelayBufferKiB { get; init; } = 64;
    internal int UdpQueueCapacity { get; init; } = 256;
    internal TimeSpan UdpSendTimeout { get; init; } = TimeSpan.FromSeconds(30);
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
            else if (key is not ("--agent-allow-source" or "--agent-allow-target-port"))
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

        if (egress == "upstream-socks5")
            Socks5Credentials.Validate(upstreamUsername, upstreamPassword);

        var logFile = Optional(values, "--log-file");
        var logMaxMb = ParsePositiveInt(Optional(values, "--log-max-mb") ?? "16", "--log-max-mb");
        var logRetain = ParsePositiveInt(Optional(values, "--log-retain") ?? "5", "--log-retain");
        if (logFile is null && (values.ContainsKey("--log-max-mb") || values.ContainsKey("--log-retain")))
            throw new ArgumentException("--log-max-mb and --log-retain require --log-file.");
        if (logMaxMb > 1024 || logRetain > 50)
            throw new ArgumentException("Log retention exceeds limits (1024 MiB, 50 files).");

        return new HostOptions {
            NetworkId = networkId,
            StateDirectory = stateDirectory,
            SocksPort = ParsePort(Optional(values, "--socks-port") ?? "1080", "--socks-port"),
            OverlayPort = ParsePort(Optional(values, "--overlay-port") ?? "42042", "--overlay-port"),
            OverlayUdpPort = ParsePort(Optional(values, "--overlay-udp-port") ?? "42043", "--overlay-udp-port"),
            AgentAllowedSources = values.GetValueOrDefault("--agent-allow-source")?
                .Select(AgentAccessPolicy.ParseSource).ToArray() ?? [],
            AgentAllowedTargetPorts = values.GetValueOrDefault("--agent-allow-target-port")?
                .Select(AgentAccessPolicy.ParsePortRange).ToArray() ?? [],
            DefaultExit = defaultExit,
            Egress = egress,
            UpstreamHost = upstreamHost,
            UpstreamPort = ParsePort(Optional(values, "--upstream-port") ?? "1080", "--upstream-port"),
            UpstreamUsername = upstreamUsername,
            UpstreamPassword = upstreamPassword,
            LogFile = logFile,
            LogMaxMegabytes = logMaxMb,
            LogRetainedFiles = logRetain,
            StartupTimeout = TimeSpan.FromSeconds(ParsePositiveInt(Optional(values, "--startup-timeout") ?? "120", "--startup-timeout")),
            ConnectTimeout = TimeSpan.FromSeconds(ParsePositiveInt(Optional(values, "--connect-timeout") ?? "20", "--connect-timeout")),
            SocksHandshakeTimeout = TimeSpan.FromSeconds(ParseBoundedTimeout(values, "--socks-handshake-timeout", 15)),
            SocksConnectReplyTimeout = TimeSpan.FromSeconds(ParseBoundedTimeout(values, "--socks-connect-reply-timeout", 25)),
            UdpIdleTimeout = TimeSpan.FromSeconds(ParsePositiveInt(Optional(values, "--udp-idle-timeout") ?? "60", "--udp-idle-timeout")),
            MaxTcpTunnels = ParseBoundedInt(values, "--max-tcp-tunnels", 128, 1, 1024),
            MaxUdpAssociations = ParseBoundedInt(values, "--max-udp-associations", 64, 1, 512),
            RelayBufferKiB = ParseBoundedInt(values, "--relay-buffer-kib", 64, 4, 256),
            UdpQueueCapacity = ParseBoundedInt(values, "--udp-queue-capacity", 256, 16, 4096),
            UdpSendTimeout = TimeSpan.FromSeconds(
                ParseBoundedInt(values, "--udp-send-timeout", 30, 1, 120)),
            ResetEventDebounce = TimeSpan.FromMilliseconds(ParsePositiveInt(Optional(values, "--reset-debounce-ms") ?? "250", "--reset-debounce-ms")),
            ResetCommandFile = Optional(values, "--reset-command-file")
        };
    }

    private static string Required(Dictionary<string, List<string>> values, string key)
        => Optional(values, key) ?? throw new ArgumentException($"Missing required argument {key}.");

    private static string? Optional(Dictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var list) && list.Count != 0 ? list[^1] : null;

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

    private static int ParseBoundedTimeout(
        Dictionary<string, List<string>> values, string name, int defaultSeconds)
    {
        var seconds = ParsePositiveInt(
            Optional(values, name) ?? defaultSeconds.ToString(), name);
        if (seconds > 120)
            throw new ArgumentException($"{name} must be 1..120 seconds.");
        return seconds;
    }

    private static int ParseBoundedInt(
        Dictionary<string, List<string>> values,
        string name, int defaultValue, int minValue, int maxValue)
    {
        var parsed = ParsePositiveInt(
            Optional(values, name) ?? defaultValue.ToString(), name);
        if (parsed < minValue || parsed > maxValue)
            throw new ArgumentException($"{name} must be {minValue}..{maxValue}.");
        return parsed;
    }

    internal static string Usage =>
        """
        netloop --network <hex> --state-dir <dir> --default-exit <managed-ip> [options]

        Options:
          --socks-port <port>         Local SOCKS5 port (default 1080)
          --overlay-port <port>       NetLoop peer Agent TCP port (default 42042)
          --overlay-udp-port <port>   NetLoop peer Agent UDP port (default 42043)
          --agent-allow-source <IP/CIDR> Repeatable inbound Agent source ACL (default all network members)
          --agent-allow-target-port <PORT|START-END> Repeatable Agent destination port ACL
          --default-exit <managed-ip> Required sole IPv4 Managed IP of default egress peer
          --egress <mode>             direct | upstream-socks5 (default direct)
          --upstream-host <host>      Upstream SOCKS5 host
          --upstream-port <port>      Upstream SOCKS5 port (default 1080; upstream mode only)
          --upstream-user <user>      RFC1929 username; requires password
          --upstream-password <pass>  RFC1929 password; requires username
          --log-file <path>           Optional rotating JSONL file (stdout by default)
          --log-max-mb <MiB>          Maximum file size (default 16, range 1..1024)
          --log-retain <count>        Retained rotated files (default 5, range 1..50)
          --startup-timeout <sec>     libzt startup timeout (default 120)
          --connect-timeout <sec>     Local/egress connect timeout (default 20)
          --socks-handshake-timeout <sec> Client SOCKS5 handshake deadline (default 15)
          --socks-connect-reply-timeout <sec> Upstream CONNECT reply deadline (default 25)
          --max-tcp-tunnels <count>   Per-listener TCP connection cap (default 128; 1..1024)
          --max-udp-associations <count> Per-entry UDP association cap (default 64; 1..512)
          --relay-buffer-kib <KiB>   Buffer per relay direction (default 64; 4..256)
          --udp-queue-capacity <count> Per-transport UDP receive queue cap (default 256; 16..4096)
          --udp-send-timeout <sec>   Overlay UDP per-datagram forwarding deadline (default 30; 1..120)
          --udp-idle-timeout <sec>    UDP association idle timeout (default 60)
          --reset-debounce-ms <ms>    Coalesce OS network-change events (default 250)
          --reset-command-file <path> Optional deterministic reset trigger input
          --version
          --help
        """;
}
