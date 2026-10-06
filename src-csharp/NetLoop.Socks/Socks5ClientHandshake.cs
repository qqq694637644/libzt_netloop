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
        Socks5Credentials.Validate(username, password);

        byte[]? usernameBytes = null;
        byte[]? passwordBytes = null;
        var requestedMethod = Socks5Protocol.NoAuthentication;
        if (username is not null)
        {
            usernameBytes = Encoding.UTF8.GetBytes(username);
            passwordBytes = Encoding.UTF8.GetBytes(password!);
            requestedMethod = Socks5Protocol.UsernamePassword;
        }

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
                usernameBytes!,
                passwordBytes!,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask AuthenticateUsernamePasswordAsync(
        IProxyConnection connection,
        byte[] username,
        byte[] password,
        CancellationToken cancellationToken)
    {
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
