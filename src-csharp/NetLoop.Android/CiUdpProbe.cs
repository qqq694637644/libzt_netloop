#if NETLOOP_CI
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Android.Content;

namespace NetLoop.Android;

internal sealed record CiUdpProbeRequest(
    string RequestId,
    string Mode,
    IPAddress PeerAddress,
    ushort PeerPort,
    string StunHost,
    ushort StunPort,
    IPAddress? ExpectedPublicIp,
    int TimeoutMilliseconds,
    byte[] Content)
{
    internal static CiUdpProbeRequest FromIntent(Intent intent)
    {
        var requestId = (
            intent.GetStringExtra("request_id")
            ?? throw new InvalidOperationException(
                "CI UDP probe requires request_id."))
            .Trim();
        if (requestId.Length == 0)
            throw new InvalidOperationException(
                "CI UDP probe requires a non-empty request_id.");

        var mode = (intent.GetStringExtra("mode") ?? "suite").Trim();
        if (mode is not ("suite" or "peer"))
            throw new InvalidOperationException(
                $"Unsupported CI UDP probe mode: {mode}");

        var peerText = (
            intent.GetStringExtra("peer_ip")
            ?? throw new InvalidOperationException(
                "CI UDP probe requires peer_ip."))
            .Trim();
        if (!IPAddress.TryParse(peerText, out var peerAddress))
            throw new InvalidOperationException(
                $"Invalid CI UDP probe peer_ip: {peerText}");

        var peerPort = ReadPort(intent, "peer_port");
        var stunHost = (
            intent.GetStringExtra("stun_host")
            ?? "stun.l.google.com")
            .Trim();
        var stunPort = ReadPort(intent, "stun_port", 19302);

        IPAddress? expectedPublicIp = null;
        var expectedText = (
            intent.GetStringExtra("expected_public_ip")
            ?? string.Empty)
            .Trim();
        if (expectedText.Length != 0
            && !IPAddress.TryParse(expectedText, out expectedPublicIp))
        {
            throw new InvalidOperationException(
                "Invalid CI UDP probe expected_public_ip: "
                + expectedText);
        }

        var timeoutMilliseconds = intent.GetIntExtra(
            "timeout_ms",
            mode == "peer" ? 750 : 5000);
        if (timeoutMilliseconds is < 100 or > 30000)
        {
            throw new InvalidOperationException(
                "CI UDP probe timeout_ms must be between 100 and 30000.");
        }

        var contentText = intent.GetStringExtra("content");
        var content = Encoding.ASCII.GetBytes(
            string.IsNullOrEmpty(contentText)
                ? "netloop-peer-udp-echo"
                : contentText);

        return new CiUdpProbeRequest(
            requestId,
            mode,
            peerAddress,
            peerPort,
            stunHost,
            stunPort,
            expectedPublicIp,
            timeoutMilliseconds,
            content);
    }

    private static ushort ReadPort(
        Intent intent,
        string name,
        int defaultValue = 0)
    {
        var value = intent.GetIntExtra(name, defaultValue);
        if (value is <= 0 or > ushort.MaxValue)
            throw new InvalidOperationException(
                $"Invalid CI UDP probe {name}: {value}");
        return (ushort)value;
    }
}

internal sealed class CiUdpProbeResult
{
    public required string request_id { get; init; }
    public required string mode { get; init; }
    public bool success { get; set; }
    public bool peer_local_udp { get; set; }
    public bool udp_frag_rejected { get; set; }
    public string? observed_public_ip { get; set; }
    public bool udp_default_exit_matches { get; set; }
    public bool udp_control_close_cleanup { get; set; }
    public double elapsed_ms { get; set; }
    public string? error_type { get; set; }
    public string? error { get; set; }
}

internal sealed record SocksUdpResponse(
    string Host,
    ushort Port,
    byte[] Content);

internal static class CiUdpProbe
{
    private const uint StunMagicCookie = 0x2112A442;
    private static readonly byte[] StunTransactionId =
        Encoding.ASCII.GetBytes("netloopci001");

    internal static CiUdpProbeResult Run(CiUdpProbeRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        var result = new CiUdpProbeResult {
            request_id = request.RequestId,
            mode = request.Mode
        };

        try
        {
            VerifyPeerLocalUdp(request);
            result.peer_local_udp = true;

            if (request.Mode == "suite")
            {
                VerifyFragRejected(request);
                result.udp_frag_rejected = true;

                var observed = UdpPublicIpViaSocks(request);
                result.observed_public_ip = observed.ToString();
                result.udp_default_exit_matches =
                    request.ExpectedPublicIp is null
                    || observed.Equals(request.ExpectedPublicIp);
                if (!result.udp_default_exit_matches)
                {
                    throw new InvalidOperationException(
                        "UDP default_exit public IP mismatch: "
                        + $"{observed} != {request.ExpectedPublicIp}");
                }

                VerifyControlCloseCleanup(request);
                result.udp_control_close_cleanup = true;
            }

            result.success = true;
        }
        catch (Exception ex)
        {
            result.error_type = ex.GetType().FullName;
            result.error = ex.Message;
        }
        finally
        {
            result.elapsed_ms =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        return result;
    }

    private static void VerifyPeerLocalUdp(CiUdpProbeRequest request)
    {
        using var association = new SocksUdpAssociation(
            request.TimeoutMilliseconds);
        var response = association.RoundTrip(
            request.PeerAddress.ToString(),
            request.PeerPort,
            request.Content,
            request.TimeoutMilliseconds);
        if (!IPAddress.Parse(response.Host).Equals(request.PeerAddress)
            || response.Port != request.PeerPort)
        {
            throw new InvalidOperationException(
                "Peer UDP response source mismatch: "
                + $"{response.Host}:{response.Port} != "
                + $"{request.PeerAddress}:{request.PeerPort}");
        }
        if (!response.Content.AsSpan().SequenceEqual(request.Content))
        {
            throw new InvalidOperationException(
                "Peer UDP echo content mismatch.");
        }
    }

    private static void VerifyFragRejected(CiUdpProbeRequest request)
    {
        using var association = new SocksUdpAssociation(
            request.TimeoutMilliseconds);
        association.Send(
            request.PeerAddress.ToString(),
            request.PeerPort,
            request.Content,
            frag: 1);

        var rejected = false;
        try
        {
            _ = association.Receive(Math.Min(1000, request.TimeoutMilliseconds));
        }
        catch (SocketException ex)
            when (ex.SocketErrorCode is SocketError.TimedOut
                or SocketError.WouldBlock)
        {
            rejected = true;
        }
        if (!rejected)
        {
            throw new InvalidOperationException(
                "SOCKS5 UDP FRAG != 0 unexpectedly produced a response.");
        }

        var response = association.RoundTrip(
            request.PeerAddress.ToString(),
            request.PeerPort,
            request.Content,
            request.TimeoutMilliseconds);
        if (!response.Content.AsSpan().SequenceEqual(request.Content))
        {
            throw new InvalidOperationException(
                "UDP association did not recover after FRAG rejection.");
        }
    }

    private static IPAddress UdpPublicIpViaSocks(
        CiUdpProbeRequest request)
    {
        var stunRequest = BuildStunBindingRequest();
        using var association = new SocksUdpAssociation(
            Math.Max(request.TimeoutMilliseconds, 5000));
        var response = association.RoundTrip(
            request.StunHost,
            request.StunPort,
            stunRequest,
            Math.Max(request.TimeoutMilliseconds, 5000));
        return ParseStunPublicIp(response.Content);
    }

    private static void VerifyControlCloseCleanup(
        CiUdpProbeRequest request)
    {
        using var association = new SocksUdpAssociation(
            request.TimeoutMilliseconds);
        var response = association.RoundTrip(
            request.PeerAddress.ToString(),
            request.PeerPort,
            request.Content,
            request.TimeoutMilliseconds);
        if (!response.Content.AsSpan().SequenceEqual(request.Content))
        {
            throw new InvalidOperationException(
                "UDP association did not work before control close.");
        }

        association.CloseControl();
        Thread.Sleep(1000);
        association.Send(
            request.PeerAddress.ToString(),
            request.PeerPort,
            request.Content);

        try
        {
            _ = association.Receive(1500);
        }
        catch (SocketException ex)
            when (ex.SocketErrorCode is SocketError.TimedOut
                or SocketError.WouldBlock
                or SocketError.ConnectionReset)
        {
            return;
        }

        throw new InvalidOperationException(
            "UDP association still relayed traffic after control TCP closed.");
    }

    private static byte[] BuildStunBindingRequest()
    {
        var request = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(0, 2),
            0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(2, 2),
            0);
        BinaryPrimitives.WriteUInt32BigEndian(
            request.AsSpan(4, 4),
            StunMagicCookie);
        StunTransactionId.CopyTo(request, 8);
        return request;
    }

    private static IPAddress ParseStunPublicIp(byte[] content)
    {
        if (content.Length < 20)
            throw new InvalidOperationException(
                "Truncated STUN response.");

        var messageType = BinaryPrimitives.ReadUInt16BigEndian(
            content.AsSpan(0, 2));
        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(
            content.AsSpan(2, 2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(
            content.AsSpan(4, 4));

        if (messageType != 0x0101)
            throw new InvalidOperationException(
                $"Unexpected STUN message type 0x{messageType:x4}.");
        if (cookie != StunMagicCookie)
            throw new InvalidOperationException(
                $"Unexpected STUN magic cookie 0x{cookie:x8}.");
        if (!content.AsSpan(8, 12).SequenceEqual(StunTransactionId))
            throw new InvalidOperationException(
                "STUN transaction ID mismatch.");

        var end = Math.Min(
            content.Length,
            20 + messageLength);
        var offset = 20;
        while (offset + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(
                content.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(
                content.AsSpan(offset + 2, 2));
            var valueStart = offset + 4;
            var valueEnd = valueStart + length;
            if (valueEnd > end)
            {
                throw new InvalidOperationException(
                    "Truncated STUN attribute.");
            }

            var value = content.AsSpan(valueStart, length);
            if ((type is 0x0020 or 0x0001) && value.Length >= 8)
            {
                var family = value[1];
                if (family == 0x01 && value.Length >= 8)
                {
                    Span<byte> address = stackalloc byte[4];
                    value.Slice(4, 4).CopyTo(address);
                    if (type == 0x0020)
                    {
                        Span<byte> mask = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(
                            mask,
                            StunMagicCookie);
                        for (var i = 0; i < 4; i++)
                            address[i] ^= mask[i];
                    }
                    return new IPAddress(address);
                }

                if (family == 0x02 && value.Length >= 20)
                {
                    Span<byte> address = stackalloc byte[16];
                    value.Slice(4, 16).CopyTo(address);
                    if (type == 0x0020)
                    {
                        Span<byte> mask = stackalloc byte[16];
                        BinaryPrimitives.WriteUInt32BigEndian(
                            mask.Slice(0, 4),
                            StunMagicCookie);
                        StunTransactionId.CopyTo(mask.Slice(4, 12));
                        for (var i = 0; i < 16; i++)
                            address[i] ^= mask[i];
                    }
                    return new IPAddress(address);
                }
            }

            offset = valueStart + ((length + 3) & ~3);
        }

        throw new InvalidOperationException(
            "STUN response did not contain a mapped address.");
    }
}

internal sealed class SocksUdpAssociation : IDisposable
{
    private readonly TcpClient _control;
    private readonly NetworkStream _stream;
    private readonly Socket _udp;
    private readonly IPEndPoint _relay;
    private bool _controlClosed;

    internal SocksUdpAssociation(int timeoutMilliseconds)
    {
        _control = new TcpClient(AddressFamily.InterNetwork);
        _control.ReceiveTimeout = timeoutMilliseconds;
        _control.SendTimeout = timeoutMilliseconds;
        _control.Connect(IPAddress.Loopback, 1080);
        _stream = _control.GetStream();
        _stream.ReadTimeout = timeoutMilliseconds;
        _stream.WriteTimeout = timeoutMilliseconds;

        try
        {
            _stream.Write([0x05, 0x01, 0x00]);
            var method = ReadExactly(2);
            if (method[0] != 0x05 || method[1] != 0x00)
            {
                throw new InvalidOperationException(
                    "SOCKS5 UDP method negotiation failed.");
            }

            _stream.Write([
                0x05, 0x03, 0x00,
                0x01, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00
            ]);

            var header = ReadExactly(4);
            if (header[0] != 0x05 || header[1] != 0x00)
            {
                throw new InvalidOperationException(
                    "SOCKS5 UDP ASSOCIATE failed: "
                    + Convert.ToHexString(header));
            }

            _relay = ReadEndpoint(header[3]);
            _udp = new Socket(
                _relay.AddressFamily,
                SocketType.Dgram,
                ProtocolType.Udp);
            _udp.Bind(
                new IPEndPoint(
                    _relay.AddressFamily == AddressFamily.InterNetworkV6
                        ? IPAddress.IPv6Loopback
                        : IPAddress.Loopback,
                    0));
            _udp.ReceiveTimeout = timeoutMilliseconds;
            _udp.SendTimeout = timeoutMilliseconds;
        }
        catch
        {
            _stream.Dispose();
            _control.Dispose();
            throw;
        }
    }

    internal void Send(
        string targetHost,
        ushort targetPort,
        byte[] content,
        byte frag = 0)
    {
        var target = EncodeTarget(targetHost, targetPort);
        var packet = new byte[3 + target.Length + content.Length];
        packet[0] = 0;
        packet[1] = 0;
        packet[2] = frag;
        target.CopyTo(packet, 3);
        content.CopyTo(packet, 3 + target.Length);
        _udp.SendTo(packet, _relay);
    }

    internal SocksUdpResponse Receive(int timeoutMilliseconds)
    {
        _udp.ReceiveTimeout = timeoutMilliseconds;
        var buffer = new byte[65535];
        EndPoint remote = new IPEndPoint(
            _relay.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any,
            0);
        var count = _udp.ReceiveFrom(buffer, ref remote);
        if (remote is not IPEndPoint endpoint
            || !endpoint.Address.Equals(_relay.Address)
            || endpoint.Port != _relay.Port)
        {
            throw new InvalidOperationException(
                "SOCKS5 UDP response came from an unexpected relay.");
        }
        if (count < 7 || buffer[0] != 0 || buffer[1] != 0)
            throw new InvalidOperationException(
                "Invalid SOCKS5 UDP response.");
        if (buffer[2] != 0)
            throw new InvalidOperationException(
                $"Unexpected SOCKS5 UDP response FRAG={buffer[2]}.");

        var (host, port, contentOffset) = ReadTarget(
            buffer.AsSpan(3, count - 3));
        return new SocksUdpResponse(
            host,
            port,
            buffer
                .AsSpan(3 + contentOffset, count - 3 - contentOffset)
                .ToArray());
    }

    internal SocksUdpResponse RoundTrip(
        string targetHost,
        ushort targetPort,
        byte[] content,
        int timeoutMilliseconds)
    {
        Send(targetHost, targetPort, content);
        return Receive(timeoutMilliseconds);
    }

    internal void CloseControl()
    {
        if (_controlClosed)
            return;
        _controlClosed = true;
        try
        {
            _control.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        _stream.Dispose();
        _control.Dispose();
    }

    public void Dispose()
    {
        CloseControl();
        _udp.Dispose();
    }

    private byte[] ReadExactly(int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = _stream.Read(
                buffer,
                offset,
                buffer.Length - offset);
            if (read == 0)
                throw new EndOfStreamException(
                    "SOCKS5 control connection closed.");
            offset += read;
        }
        return buffer;
    }

    private IPEndPoint ReadEndpoint(byte addressType)
    {
        IPAddress address;
        switch (addressType)
        {
            case 0x01:
                address = new IPAddress(ReadExactly(4));
                break;
            case 0x04:
                address = new IPAddress(ReadExactly(16));
                break;
            case 0x03:
            {
                var length = ReadExactly(1)[0];
                var host = Encoding.ASCII.GetString(
                    ReadExactly(length));
                var resolved = Dns.GetHostAddresses(host);
                address = resolved.FirstOrDefault(
                    static candidate =>
                        candidate.AddressFamily
                        == AddressFamily.InterNetwork)
                    ?? resolved.First();
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Invalid SOCKS5 UDP relay address type {addressType}.");
        }

        var portBytes = ReadExactly(2);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        if (address.Equals(IPAddress.Any))
            address = IPAddress.Loopback;
        else if (address.Equals(IPAddress.IPv6Any))
            address = IPAddress.IPv6Loopback;
        return new IPEndPoint(address, port);
    }

    private static byte[] EncodeTarget(
        string host,
        ushort port)
    {
        byte[] address;
        if (IPAddress.TryParse(host, out var parsed))
        {
            var bytes = parsed.GetAddressBytes();
            address = new byte[1 + bytes.Length + 2];
            address[0] = parsed.AddressFamily
                == AddressFamily.InterNetwork
                ? (byte)0x01
                : (byte)0x04;
            bytes.CopyTo(address, 1);
        }
        else
        {
            var hostBytes = Encoding.ASCII.GetBytes(host);
            if (hostBytes.Length is 0 or > 255)
                throw new InvalidOperationException(
                    "SOCKS5 domain length is invalid.");
            address = new byte[2 + hostBytes.Length + 2];
            address[0] = 0x03;
            address[1] = (byte)hostBytes.Length;
            hostBytes.CopyTo(address, 2);
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            address.AsSpan(address.Length - 2, 2),
            port);
        return address;
    }

    private static (string Host, ushort Port, int ContentOffset) ReadTarget(
        ReadOnlySpan<byte> content)
    {
        if (content.Length < 1)
            throw new InvalidOperationException(
                "Truncated SOCKS5 UDP target.");

        string host;
        int offset;
        switch (content[0])
        {
            case 0x01:
                if (content.Length < 5)
                    throw new InvalidOperationException(
                        "Truncated SOCKS5 UDP IPv4 target.");
                host = new IPAddress(content.Slice(1, 4)).ToString();
                offset = 5;
                break;

            case 0x04:
                if (content.Length < 17)
                    throw new InvalidOperationException(
                        "Truncated SOCKS5 UDP IPv6 target.");
                host = new IPAddress(content.Slice(1, 16)).ToString();
                offset = 17;
                break;

            case 0x03:
                if (content.Length < 2)
                    throw new InvalidOperationException(
                        "Truncated SOCKS5 UDP domain target.");
                var length = content[1];
                if (content.Length < 2 + length)
                    throw new InvalidOperationException(
                        "Truncated SOCKS5 UDP domain target.");
                host = Encoding.ASCII.GetString(
                    content.Slice(2, length));
                offset = 2 + length;
                break;

            default:
                throw new InvalidOperationException(
                    $"Invalid SOCKS5 UDP target type {content[0]}.");
        }

        if (content.Length < offset + 2)
            throw new InvalidOperationException(
                "Truncated SOCKS5 UDP target port.");
        var port = BinaryPrimitives.ReadUInt16BigEndian(
            content.Slice(offset, 2));
        return (host, port, offset + 2);
    }
}

internal static class CiUdpProbeStatus
{
    private const string FileName = "netloop-ci-udp-probe.json";

    internal static string GetExternalPath(Context context)
    {
        var directory = context.GetExternalFilesDir(null)
            ?? throw new InvalidOperationException(
                "Android external files directory is unavailable.");
        return Path.Combine(directory.AbsolutePath, FileName);
    }

    internal static void Write(
        Context context,
        CiUdpProbeResult content)
    {
        var path = GetExternalPath(context);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Unable to resolve NetLoop CI UDP probe directory.");
        Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                content,
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
        File.Move(temp, path, true);
    }
}
#endif
