using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetLoop.Core;

namespace NetLoop.Socks;

public sealed class Socks5ConnectionHandler
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(15);
    private readonly IProxyConnector _connector;
    private readonly ISocks5UdpAssociationFactory? _udpAssociationFactory;
    private readonly TimeSpan _handshakeTimeout;

    public Socks5ConnectionHandler(
        IProxyConnector connector,
        ISocks5UdpAssociationFactory? udpAssociationFactory = null,
        TimeSpan? handshakeTimeout = null)
    {
        _connector = connector;
        _udpAssociationFactory = udpAssociationFactory;
        _handshakeTimeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        if (_handshakeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
    }

    public async Task HandleAsync(IProxyConnection client, CancellationToken cancellationToken)
    {
        var connectionId = client is IConnectionTrace traced
            ? traced.ConnectionId
            : ConnectionTrace.NewId();
        using var traceScope = ConnectionTrace.EnterInbound(connectionId);

        using var handshakeCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(_handshakeTimeout);
        var handshakeStarted = Stopwatch.GetTimestamp();
        ParsedRequest? parsed;
        try
        {
            parsed = await ReadClientRequestAsync(
                client, handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                  && handshakeCts.IsCancellationRequested)
        {
            JsonLog.Error("socks_handshake_timeout", new {
                connection_id = connectionId,
                client = client.Description,
                timeout_ms = _handshakeTimeout.TotalMilliseconds,
                elapsed_ms = Stopwatch.GetElapsedTime(handshakeStarted).TotalMilliseconds
            });
            throw new TimeoutException("SOCKS5 client handshake deadline expired.");
        }
        // Subsequent relay and UDP control traffic must not inherit the
        // handshake timer.
        handshakeCts.CancelAfter(Timeout.InfiniteTimeSpan);

        if (parsed is null)
            return;
        if (parsed.Value.Command == Socks5Protocol.UdpAssociate)
        {
            await HandleUdpAssociationAsync(
                client, parsed.Value.Target, cancellationToken).ConfigureAwait(false);
            return;
        }

        var target = parsed.Value.Target;
        // A parsed SOCKS5 request is distinct from the earlier TCP accept
        // event; both carry the same ID on an overlay Agent.
        JsonLog.Info("socks_connect_request", new {
            connection_id = connectionId,
            target = target.ToString(),
            client = client.Description
        });

        IProxyConnection? remote = null;
        try
        {
            remote = await _connector.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            var success = Socks5Protocol.BuildReply(Socks5Protocol.ReplySucceeded);
            await client.WriteAsync(success, success.Length, cancellationToken).ConfigureAwait(false);
            await ConnectionRelay.RunAsync(client, remote, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                connection_id = connectionId,
                client = client.Description,
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

    private async Task<ParsedRequest?> ReadClientRequestAsync(
        IProxyConnection client,
        CancellationToken handshakeToken)
    {
        var greeting = new byte[2];
        await Socks5Protocol.ReadExactlyAsync(client, greeting, 2, handshakeToken).ConfigureAwait(false);
        if (greeting[0] != Socks5Protocol.Version || greeting[1] == 0)
            throw new ProtocolViolationException("Invalid SOCKS5 greeting.");

        var methods = new byte[greeting[1]];
        await Socks5Protocol.ReadExactlyAsync(client, methods, methods.Length, handshakeToken).ConfigureAwait(false);
        if (!methods.Contains(Socks5Protocol.NoAuthentication))
        {
            var rejected = new byte[] { Socks5Protocol.Version, Socks5Protocol.NoAcceptableMethods };
            await client.WriteAsync(rejected, rejected.Length, handshakeToken).ConfigureAwait(false);
            return null;
        }

        var selected = new byte[] { Socks5Protocol.Version, Socks5Protocol.NoAuthentication };
        await client.WriteAsync(selected, selected.Length, handshakeToken).ConfigureAwait(false);

        var header = new byte[4];
        await Socks5Protocol.ReadExactlyAsync(client, header, 4, handshakeToken).ConfigureAwait(false);
        if (header[0] != Socks5Protocol.Version || header[2] != 0)
            throw new ProtocolViolationException("Invalid SOCKS5 request header.");

        if (header[1] == Socks5Protocol.UdpAssociate)
        {
            if (_udpAssociationFactory is null)
            {
                var unsupported = Socks5Protocol.BuildReply(Socks5Protocol.ReplyCommandNotSupported);
                await client.WriteAsync(unsupported, unsupported.Length, handshakeToken).ConfigureAwait(false);
                return null;
            }

            ProxyTarget declaredEndpoint;
            try
            {
                declaredEndpoint = await Socks5Protocol.ReadTargetAsync(
                    client,
                    header[3],
                    handshakeToken,
                    allowZeroPort: true).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                var unsupported = Socks5Protocol.BuildReply(Socks5Protocol.ReplyAddressTypeNotSupported);
                await client.WriteAsync(unsupported, unsupported.Length, handshakeToken).ConfigureAwait(false);
                return null;
            }

            return new ParsedRequest(header[1], declaredEndpoint);
        }

        if (header[1] != Socks5Protocol.Connect)
        {
            var unsupported = Socks5Protocol.BuildReply(Socks5Protocol.ReplyCommandNotSupported);
            await client.WriteAsync(unsupported, unsupported.Length, handshakeToken).ConfigureAwait(false);
            return null;
        }

        ProxyTarget target;
        try
        {
            target = await Socks5Protocol.ReadTargetAsync(client, header[3], handshakeToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            var unsupported = Socks5Protocol.BuildReply(Socks5Protocol.ReplyAddressTypeNotSupported);
            await client.WriteAsync(unsupported, unsupported.Length, handshakeToken).ConfigureAwait(false);
            return null;
        }

        return new ParsedRequest(header[1], target);
    }

    private readonly record struct ParsedRequest(byte Command, ProxyTarget Target);

    private async Task HandleUdpAssociationAsync(
        IProxyConnection client,
        ProxyTarget declaredEndpoint,
        CancellationToken cancellationToken)
    {
        ISocks5UdpAssociation association;
        try
        {
            association = await _udpAssociationFactory!.CreateAsync(
                client,
                declaredEndpoint,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = Socks5Protocol.BuildReply(Socks5Protocol.ReplyGeneralFailure);
            await client.WriteAsync(failed, failed.Length, cancellationToken).ConfigureAwait(false);
            JsonLog.Error("socks_udp_associate_failed", new {
                client = client.RemoteEndPoint?.ToString(),
                declared = declaredEndpoint.ToString(),
                error_type = ex.GetType().Name,
                error = ex.Message
            });
            return;
        }

        await using var associationLifetime = association;

        var reply = Socks5Protocol.BuildReply(
            Socks5Protocol.ReplySucceeded,
            association.RelayEndPoint);
        await client.WriteAsync(reply, reply.Length, cancellationToken).ConfigureAwait(false);

        JsonLog.Info("socks_udp_associate_ready", new {
            client = client.RemoteEndPoint?.ToString(),
            declared = declaredEndpoint.ToString(),
            relay = association.RelayEndPoint.ToString()
        });

        using var controlWaitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controlClosed = WaitForControlCloseAsync(client, controlWaitCts.Token);
        var completed = await Task.WhenAny(
            controlClosed,
            association.Completion).ConfigureAwait(false);

        if (ReferenceEquals(completed, association.Completion))
        {
            await controlWaitCts.CancelAsync().ConfigureAwait(false);
            await association.Completion.ConfigureAwait(false);
            try
            {
                await controlClosed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (controlWaitCts.IsCancellationRequested)
            {
            }
        }
        else
        {
            await controlClosed.ConfigureAwait(false);
        }
    }

    private static async Task WaitForControlCloseAsync(
        IProxyConnection client,
        CancellationToken cancellationToken)
    {
        var oneByte = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await client.ReadAsync(
                oneByte,
                oneByte.Length,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;
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
