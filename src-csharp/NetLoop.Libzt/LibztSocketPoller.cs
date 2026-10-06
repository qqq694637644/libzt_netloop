namespace NetLoop.Libzt;

internal static class LibztSocketPoller
{
    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromMilliseconds(10);

    internal static async ValueTask WaitAsync(
        int fd,
        short events,
        CancellationToken cancellationToken,
        object nativeGate,
        Func<int> currentFdProvider,
        string disposedObjectName)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = new LibztNative.PollFd {
                Fd = fd,
                Events = events,
                Revents = 0
            };
            int result;
            int socketError = 0;
            lock (nativeGate)
            {
                EnsureCurrentFd(
                    fd,
                    currentFdProvider,
                    cancellationToken,
                    disposedObjectName);
                result = LibztNative.Poll(
                    ref descriptor,
                    1,
                    0);

                var requestedReady = result > 0
                    && (descriptor.Revents & events) != 0;
                var hasPollError = result > 0
                    && (descriptor.Revents
                        & (LibztNative.PollError | LibztNative.PollInvalid)) != 0;
                if (result < 0 || (!requestedReady && hasPollError))
                {
                    socketError = LibztNative.GetLastSocketError(fd);
                }
            }

            if (result < 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new LibztException(
                    "zts_bsd_poll",
                    result,
                    socketError);
            }

            if (result > 0)
            {
                if ((descriptor.Revents & events) != 0)
                    return;

                var hasPollError = (descriptor.Revents
                    & (LibztNative.PollError | LibztNative.PollInvalid)) != 0;

                if (hasPollError)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(cancellationToken);

                    if (socketError == 0)
                    {
                        throw new IOException(
                            "libzt socket poll reported an error "
                            + $"for fd {fd} (revents=0x{descriptor.Revents:x}).");
                    }

                    throw new LibztException(
                        "zts_bsd_poll(revents)",
                        LibztNative.ErrSocket,
                        socketError);
                }
            }

            await Task.Delay(
                RetryDelay,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureCurrentFd(
        int expectedFd,
        Func<int> currentFdProvider,
        CancellationToken cancellationToken,
        string disposedObjectName)
    {
        if (currentFdProvider() == expectedFd)
            return;

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        throw new ObjectDisposedException(disposedObjectName);
    }

    internal static bool IsWouldBlock(int error)
        => error is LibztNative.EAgain or LibztNative.WindowsEWouldBlock;
}
