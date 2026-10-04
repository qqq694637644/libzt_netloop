using System.Text;
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
            var requestedMethod = _username is null
                ? Socks5Protocol.NoAuthentication
                : Socks5Protocol.UsernamePassword;
            var greeting = new byte[] { Socks5Protocol.Version, 1, requestedMethod };
            await connection.WriteAsync(greeting, greeting.Length, cancellationToken).ConfigureAwait(false);

            var methodReply = new byte[2];
            await Socks5Protocol.ReadExactlyAsync(connection, methodReply, 2, cancellationToken).ConfigureAwait(false);
            if (methodReply[0] != Socks5Protocol.Version || methodReply[1] != requestedMethod)
                throw new UnauthorizedAccessException(
                    $"SOCKS5 proxy rejected requested authentication method {requestedMethod}.");

            if (requestedMethod == Socks5Protocol.UsernamePassword)
                await AuthenticateAsync(connection, cancellationToken).ConfigureAwait(false);

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

    private async ValueTask AuthenticateAsync(
        IProxyConnection connection,
        CancellationToken cancellationToken)
    {
        var username = Encoding.UTF8.GetBytes(_username ?? string.Empty);
        var password = Encoding.UTF8.GetBytes(_password ?? string.Empty);
        if (username.Length is 0 or > 255 || password.Length > 255)
            throw new ArgumentException("SOCKS5 username must be 1..255 bytes and password at most 255 bytes.");

        var request = new byte[3 + username.Length + password.Length];
        var offset = 0;
        request[offset++] = 1;
        request[offset++] = checked((byte)username.Length);
        username.CopyTo(request, offset);
        offset += username.Length;
        request[offset++] = checked((byte)password.Length);
        password.CopyTo(request, offset);

        await connection.WriteAsync(request, request.Length, cancellationToken).ConfigureAwait(false);
        var reply = new byte[2];
        await Socks5Protocol.ReadExactlyAsync(connection, reply, 2, cancellationToken).ConfigureAwait(false);
        if (reply[0] != 1 || reply[1] != 0)
            throw new UnauthorizedAccessException("SOCKS5 username/password authentication failed.");
    }
}
