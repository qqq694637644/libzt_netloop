using System.Buffers;

namespace NetLoop.Core;

public static class ConnectionRelay
{
    public const int BufferSize = 64 * 1024;

    public static async Task RunAsync(
        IProxyConnection left,
        IProxyConnection right,
        CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftToRight = PumpAsync(left, right, relayCts.Token);
        var rightToLeft = PumpAsync(right, left, relayCts.Token);

        var first = await Task.WhenAny(leftToRight, rightToLeft).ConfigureAwait(false);
        var second = ReferenceEquals(first, leftToRight) ? rightToLeft : leftToRight;
        try
        {
            await first.ConfigureAwait(false);
        }
        catch
        {
            await relayCts.CancelAsync().ConfigureAwait(false);
            await AwaitAfterCancelAsync(second, relayCts.Token).ConfigureAwait(false);
            throw;
        }

        try
        {
            await second.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await relayCts.CancelAsync().ConfigureAwait(false);
            await AwaitAfterCancelAsync(second, relayCts.Token).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task AwaitAfterCancelAsync(
        Task pump,
        CancellationToken cancellationToken)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
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
