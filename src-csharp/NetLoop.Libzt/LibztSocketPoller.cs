namespace NetLoop.Libzt;

internal static class LibztSocketPoller
{
    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromMilliseconds(10);

    internal static async ValueTask WaitAsync(
        int fd,
        short events,
        CancellationToken cancellationToken,
        object? nativeGate = null,
        Func<int>? currentFdProvider = null,
        string disposedObjectName = "libzt socket")
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
            if (nativeGate is null)
            {
                result = PollOnce(
                    fd,
                    ref descriptor,
                    out socketError);
            }
            else
            {
                lock (nativeGate)
                {
                    EnsureCurrentFd(
                        fd,
                        currentFdProvider,
                        cancellationToken,
                        disposedObjectName);
                    result = PollOnce(
                        fd,
                        ref descriptor,
                        out socketError);
                }
            }

            if (result < 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                throw new LibztException(
                    "zts_bsd_poll",
                    result,
                    nativeGate is null
                        ? LibztNative.GetErrno()
                        : socketError);
            }

            if (result > 0)
            {
                var hasPollError = (descriptor.Revents
                    & (LibztNative.PollError | LibztNative.PollInvalid)) != 0;

                if (nativeGate is not null && hasPollError)
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

                if ((descriptor.Revents & events) != 0)
                    return;

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
        Func<int>? currentFdProvider,
        CancellationToken cancellationToken,
        string disposedObjectName)
    {
        if (currentFdProvider is null
            || currentFdProvider() == expectedFd)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        throw new ObjectDisposedException(disposedObjectName);
    }

    private static int PollOnce(
        int fd,
        ref LibztNative.PollFd descriptor,
        out int socketError)
    {
        var result = LibztNative.Poll(
            ref descriptor,
            1,
            0);
        var hasSocketError = result < 0
            || (descriptor.Revents
                & (LibztNative.PollError | LibztNative.PollInvalid)) != 0;
        socketError = hasSocketError
            ? LibztNative.GetLastSocketError(fd)
            : 0;
        return result;
    }

    internal static bool IsWouldBlock(int error)
        => error is LibztNative.EAgain or LibztNative.WindowsEWouldBlock;
}
