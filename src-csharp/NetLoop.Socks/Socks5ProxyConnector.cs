using System.Net;
using NetLoop.Core;

namespace NetLoop.Socks;

public sealed class Socks5ProxyConnector : IProxyConnector
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly IProxyConnector _transportConnector;
    private readonly ProxyTarget _proxyEndpoint;
    private readonly string? _username;
    private readonly string? _password;
    private readonly TimeSpan? _negotiationRetryWindow;
    private readonly TimeSpan _negotiationAttemptTimeout;

    public Socks5ProxyConnector(
        IProxyConnector transportConnector,
        ProxyTarget proxyEndpoint,
        string? username = null,
        string? password = null,
        TimeSpan? negotiationRetryWindow = null,
        TimeSpan? negotiationAttemptTimeout = null)
    {
        _transportConnector = transportConnector;
        _proxyEndpoint = proxyEndpoint;
        _username = username;
        _password = password;
        _negotiationRetryWindow = negotiationRetryWindow;
        _negotiationAttemptTimeout =
            negotiationAttemptTimeout ?? TimeSpan.FromSeconds(1);
    }

    public async ValueTask<IProxyConnection> ConnectAsync(
        ProxyTarget target,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectAndNegotiateAsync(
            cancellationToken).ConfigureAwait(false);
        try
        {
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

    private async ValueTask<IProxyConnection> ConnectAndNegotiateAsync(
        CancellationToken cancellationToken)
    {
        if (_negotiationRetryWindow is null)
            return await ConnectAndNegotiateOnceAsync(
                cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + _negotiationRetryWindow.Value;
        var attempt = 0;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var remaining = deadline - DateTimeOffset.UtcNow;
            var attemptTimeout = remaining < _negotiationAttemptTimeout
                ? remaining
                : _negotiationAttemptTimeout;
            if (attemptTimeout <= TimeSpan.Zero)
                break;

            using var attemptCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            attemptCts.CancelAfter(attemptTimeout);

            try
            {
                var connection = await ConnectAndNegotiateOnceAsync(
                    attemptCts.Token).ConfigureAwait(false);
                if (attempt > 1)
                {
                    JsonLog.Info("socks_peer_negotiation_recovered", new {
                        proxy = _proxyEndpoint.ToString(),
                        attempts = attempt
                    });
                }
                return connection;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (ProtocolViolationException)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException(
                    $"SOCKS5 peer negotiation attempt {attempt} timed out.");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            JsonLog.Info("socks_peer_negotiation_retry", new {
                proxy = _proxyEndpoint.ToString(),
                attempts = attempt,
                error = lastError?.Message
            });

            var delay = deadline - DateTimeOffset.UtcNow;
            if (delay <= TimeSpan.Zero)
                break;
            await Task.Delay(
                delay < RetryDelay ? delay : RetryDelay,
                cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"SOCKS5 peer negotiation did not recover for {_proxyEndpoint}.",
            lastError);
    }

    private async ValueTask<IProxyConnection> ConnectAndNegotiateOnceAsync(
        CancellationToken cancellationToken)
    {
        var connection = await _transportConnector.ConnectAsync(
            _proxyEndpoint,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await Socks5ClientHandshake.NegotiateAsync(
                connection,
                _username,
                _password,
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
