using System.Buffers;

namespace NetLoop.Core;

public static class ConnectionRelay
{
    public const int BufferSize = 64 * 1024;

    public static async Task RunAsync(
        IProxyConnection left,
        IProxyConnection right,
        TimeSpan halfCloseTimeout,
        CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            left.LifetimeCancellation,
            right.LifetimeCancellation);
        var leftToRight = PumpAsync(left, right, relayCts.Token);
        var rightToLeft = PumpAsync(right, left, relayCts.Token);

        var first = await Task.WhenAny(leftToRight, rightToLeft).ConfigureAwait(false);
        try
        {
            await first.ConfigureAwait(false);
        }
        catch
        {
            await relayCts.CancelAsync().ConfigureAwait(false);
            throw;
        }

        var second = ReferenceEquals(first, leftToRight) ? rightToLeft : leftToRight;
        try
        {
            await second.WaitAsync(halfCloseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            JsonLog.Info("relay_half_close_timeout", new { timeout_ms = halfCloseTimeout.TotalMilliseconds });
            await relayCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        IProxyConnection source,
        IProxyConnection destination,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    await destination.ShutdownWriteAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await destination.WriteAsync(buffer, read, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
