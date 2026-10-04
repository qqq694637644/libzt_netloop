using System.Net;
using NetLoop.Core;

namespace NetLoop.Socks;

public sealed class Socks5ProxyConnector : IProxyConnector
{
    private readonly IProxyConnector _transportConnector;
    private readonly ProxyTarget _proxyEndpoint;
    private readonly string? _username;
    private readonly string? _password;

    public Socks5ProxyConnector(
        IProxyConnector transportConnector,
        ProxyTarget proxyEndpoint,
        string? username = null,
        string? password = null)
    {
        _transportConnector = transportConnector;
        _proxyEndpoint = proxyEndpoint;
        _username = username;
        _password = password;
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        var connection = await _transportConnector.ConnectAsync(_proxyEndpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            await Socks5ClientHandshake.NegotiateAsync(
                connection,
                _username,
                _password,
                cancellationToken).ConfigureAwait(false);

            var request = Socks5Protocol.BuildTargetRequest(Socks5Protocol.Connect, target);
            await connection.WriteAsync(request, request.Length, cancellationToken).ConfigureAwait(false);

            var reply = new byte[4];
            await Socks5Protocol.ReadExactlyAsync(connection, reply, 4, cancellationToken).ConfigureAwait(false);
            if (reply[0] != Socks5Protocol.Version)
                throw new ProtocolViolationException($"Invalid SOCKS5 reply version {reply[0]}.");
            if (reply[1] != Socks5Protocol.ReplySucceeded)
                throw new IOException($"SOCKS5 CONNECT to {target} failed with reply 0x{reply[1]:x2}.");

            await Socks5Protocol.ConsumeReplyAddressAsync(connection, reply[3], cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
