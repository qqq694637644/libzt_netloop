namespace NetLoop.Libzt;

internal static class LibztSocketPoller
{
    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromMilliseconds(10);

    internal static async ValueTask WaitAsync(
        int fd,
        short events,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = new LibztNative.PollFd {
                Fd = fd,
                Events = events,
                Revents = 0
            };
            var result = LibztNative.Poll(
                ref descriptor,
                1,
                0);

            if (result < 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new LibztException(
                    "zts_bsd_poll",
                    result,
                    LibztNative.GetErrno());
            }

            if (result > 0)
            {
                // Match libzt's own C# Socket.Poll behavior: readiness for the
                // requested direction wins. The subsequent read/write/accept
                // reports EOF or the concrete socket error.
                if ((descriptor.Revents & events) != 0)
                    return;

                if ((descriptor.Revents
                    & (LibztNative.PollError | LibztNative.PollInvalid)) != 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    throw new LibztException(
                        "zts_bsd_poll(revents)",
                        LibztNative.ErrSocket,
                        LibztNative.GetLastSocketError(fd));
                }
            }

            await Task.Delay(
                RetryDelay,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsWouldBlock(int error)
        => error is LibztNative.EAgain or LibztNative.WindowsEWouldBlock;
}
