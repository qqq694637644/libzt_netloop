using System.Net;
using System.Net.Sockets;
using NetLoop.Core;

namespace NetLoop.Socks;

public sealed class Socks5ConnectionHandler
{
    private readonly IProxyConnector _connector;
    private readonly TimeSpan _halfCloseTimeout;

    public Socks5ConnectionHandler(IProxyConnector connector, TimeSpan halfCloseTimeout)
    {
        _connector = connector;
        _halfCloseTimeout = halfCloseTimeout;
    }

    public async Task HandleAsync(IProxyConnection client, CancellationToken cancellationToken)
    {
        var greeting = new byte[2];
        await Socks5Protocol.ReadExactlyAsync(client, greeting, 2, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != Socks5Protocol.Version || greeting[1] == 0)
            throw new ProtocolViolationException("Invalid SOCKS5 greeting.");

        var methods = new byte[greeting[1]];
        await Socks5Protocol.ReadExactlyAsync(client, methods, methods.Length, cancellationToken).ConfigureAwait(false);
        if (!methods.Contains(Socks5Protocol.NoAuthentication))
        {
            var rejected = new byte[] { Socks5Protocol.Version, Socks5Protocol.NoAcceptableMethods };
            await client.WriteAsync(rejected, rejected.Length, cancellationToken).ConfigureAwait(false);
            return;
        }

        var selected = new byte[] { Socks5Protocol.Version, Socks5Protocol.NoAuthentication };
        await client.WriteAsync(selected, selected.Length, cancellationToken).ConfigureAwait(false);

        var header = new byte[4];
        await Socks5Protocol.ReadExactlyAsync(client, header, 4, cancellationToken).ConfigureAwait(false);
        if (header[0] != Socks5Protocol.Version || header[2] != 0)
            throw new ProtocolViolationException("Invalid SOCKS5 request header.");

        if (header[1] != Socks5Protocol.Connect)
        {
            var unsupported = Socks5Protocol.BuildReply(Socks5Protocol.ReplyCommandNotSupported);
            await client.WriteAsync(unsupported, unsupported.Length, cancellationToken).ConfigureAwait(false);
            return;
        }

        ProxyTarget target;
        try
        {
            target = await Socks5Protocol.ReadTargetAsync(client, header[3], cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            var unsupported = Socks5Protocol.BuildReply(Socks5Protocol.ReplyAddressTypeNotSupported);
            await client.WriteAsync(unsupported, unsupported.Length, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonLog.Info("socks_connect_request", new { target = target.ToString(), client = client.Description });

        IProxyConnection? remote = null;
        try
        {
            remote = await _connector.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            var success = Socks5Protocol.BuildReply(Socks5Protocol.ReplySucceeded);
            await client.WriteAsync(success, success.Length, cancellationToken).ConfigureAwait(false);
            await ConnectionRelay.RunAsync(client, remote, _halfCloseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (remote is null)
            {
                var reply = Socks5Protocol.BuildReply(MapException(ex));
                try
                {
                    await client.WriteAsync(reply, reply.Length, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            JsonLog.Error("socks_connect_failed", new {
                target = target.ToString(),
                error_type = ex.GetType().Name,
                error = ex.Message
            });
        }
        finally
        {
            if (remote is not null)
                await remote.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static byte MapException(Exception exception)
        => exception switch {
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => Socks5Protocol.ReplyConnectionRefused,
            SocketException { SocketErrorCode: SocketError.NetworkUnreachable } => Socks5Protocol.ReplyNetworkUnreachable,
            SocketException { SocketErrorCode: SocketError.HostUnreachable } => Socks5Protocol.ReplyHostUnreachable,
            TimeoutException => Socks5Protocol.ReplyTtlExpired,
            OperationCanceledException => Socks5Protocol.ReplyTtlExpired,
            _ => Socks5Protocol.ReplyGeneralFailure
        };
}
