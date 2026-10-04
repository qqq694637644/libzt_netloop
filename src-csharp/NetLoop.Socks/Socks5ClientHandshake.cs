using System.Text;
using NetLoop.Core;

namespace NetLoop.Socks;

internal static class Socks5ClientHandshake
{
    internal static async ValueTask NegotiateAsync(
        IProxyConnection connection,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var requestedMethod = username is null
            ? Socks5Protocol.NoAuthentication
            : Socks5Protocol.UsernamePassword;
        var greeting = new byte[] { Socks5Protocol.Version, 1, requestedMethod };
        await connection.WriteAsync(greeting, greeting.Length, cancellationToken).ConfigureAwait(false);

        var methodReply = new byte[2];
        await Socks5Protocol.ReadExactlyAsync(
            connection,
            methodReply,
            methodReply.Length,
            cancellationToken).ConfigureAwait(false);

        if (methodReply[0] != Socks5Protocol.Version
            || methodReply[1] != requestedMethod)
        {
            throw new UnauthorizedAccessException(
                $"SOCKS5 proxy rejected requested authentication method {requestedMethod}.");
        }

        if (requestedMethod == Socks5Protocol.UsernamePassword)
        {
            await AuthenticateUsernamePasswordAsync(
                connection,
                username,
                password,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask AuthenticateUsernamePasswordAsync(
        IProxyConnection connection,
        string? usernameText,
        string? passwordText,
        CancellationToken cancellationToken)
    {
        var username = Encoding.UTF8.GetBytes(usernameText ?? string.Empty);
        var password = Encoding.UTF8.GetBytes(passwordText ?? string.Empty);
        if (username.Length is 0 or > 255 || password.Length > 255)
        {
            throw new ArgumentException(
                "SOCKS5 username must be 1..255 bytes and password at most 255 bytes.");
        }

        var request = new byte[3 + username.Length + password.Length];
        var offset = 0;
        request[offset++] = 1;
        request[offset++] = checked((byte)username.Length);
        username.CopyTo(request, offset);
        offset += username.Length;
        request[offset++] = checked((byte)password.Length);
        password.CopyTo(request, offset);

        await connection.WriteAsync(
            request,
            request.Length,
            cancellationToken).ConfigureAwait(false);

        var reply = new byte[2];
        await Socks5Protocol.ReadExactlyAsync(
            connection,
            reply,
            reply.Length,
            cancellationToken).ConfigureAwait(false);
        if (reply[0] != 1 || reply[1] != 0)
            throw new UnauthorizedAccessException("SOCKS5 username/password authentication failed.");
    }
}
