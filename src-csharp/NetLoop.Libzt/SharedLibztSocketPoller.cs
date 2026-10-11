namespace NetLoop.Libzt;

/// <summary>
/// Opt-in batched native poll dispatcher. Each fd is guarded by its owner's
/// nativeGate during the *entire* poll, preserving pinned lwIP's
/// LWIP_NETCONN_FULLDUPLEX=0 constraint and avoiding use-after-close / fd
/// reuse. A single timer drives up to 64 distinct fds per native poll call.
/// </summary>
internal static class SharedLibztSocketPoller
{
    private const int BatchSize = 64;
    private static readonly object Sync = new();
    private static readonly List<WaitRequest> Pending = [];
    private static bool _running;
    private static long _pollCalls;
    private static long _fdObservations;

    internal static (int Waiting, long Calls, long FdObservations) Metrics
    {
        get
        {
            lock (Sync)
                return (Pending.Count, Interlocked.Read(ref _pollCalls),
                    Interlocked.Read(ref _fdObservations));
        }
    }

    internal static async ValueTask WaitAsync(
        int fd, short events,
        CancellationToken cancellationToken, object nativeGate,
        Func<int> currentFdProvider, string disposedObjectName)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new WaitRequest(
            fd, events, cancellationToken, nativeGate,
            currentFdProvider, disposedObjectName);

        using var cancellationRegistration = cancellationToken.Register(
            static state => {
                var request = (WaitRequest)state!;
                request.Completion.TrySetCanceled(request.Token);
            }, request);

        lock (Sync)
        {
            Pending.Add(request);
            if (!_running)
            {
                _running = true;
                _ = Task.Run(PollLoopAsync);
            }
        }

        await request.Completion.Task.ConfigureAwait(false);
    }

    private static async Task PollLoopAsync()
    {
        try
        {
            while (true)
            {
                WaitRequest[] requests;
                lock (Sync)
                {
                    Pending.RemoveAll(static request => request.Completion.Task.IsCompleted);
                    if (Pending.Count == 0)
                    {
                        _running = false;
                        return;
                    }
                    requests = [.. Pending];
                }

                for (var offset = 0; offset < requests.Length; offset += BatchSize)
                    PollBatch(requests, offset, Math.Min(BatchSize, requests.Length - offset));

                await Task.Delay(10).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            WaitRequest[] abandoned;
            lock (Sync)
            {
                abandoned = [.. Pending];
                Pending.Clear();
                _running = false;
            }
            foreach (var request in abandoned)
                request.Completion.TrySetException(ex);
        }
    }

    private static void PollBatch(WaitRequest[] requests, int offset, int count)
    {
        // The dispatcher is the sole batch poller. No other code holds more
        // than one nativeGate at once. Acquire all gates in a deterministic
        // order and keep them locked from fd validation through native poll.
        var gates = new List<object>(count);
        var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var index = offset; index < offset + count; index++)
        {
            var gate = requests[index].NativeGate;
            if (unique.Add(gate))
                gates.Add(gate);
        }

        foreach (var gate in gates)
            Monitor.Enter(gate);

        try
        {
            var pollEntries = new List<LibztNative.PollFd>(count);
            var memberships = new List<List<WaitRequest>>(count);
            var fdIndex = new Dictionary<int, int>();

            for (var index = offset; index < offset + count; index++)
            {
                var request = requests[index];
                if (request.Completion.Task.IsCompleted)
                    continue;

                if (request.CurrentFd() != request.Fd)
                {
                    if (request.Token.IsCancellationRequested)
                        request.Completion.TrySetCanceled(request.Token);
                    else
                        request.Completion.TrySetException(
                            new ObjectDisposedException(request.DisposedObjectName));
                    continue;
                }

                if (fdIndex.TryGetValue(request.Fd, out var existing))
                {
                    var descriptor = pollEntries[existing];
                    descriptor.Events |= request.Events;
                    pollEntries[existing] = descriptor;
                    memberships[existing].Add(request);
                }
                else
                {
                    fdIndex.Add(request.Fd, pollEntries.Count);
                    pollEntries.Add(new LibztNative.PollFd {
                        Fd = request.Fd,
                        Events = request.Events,
                        Revents = 0
                    });
                    memberships.Add([request]);
                }
            }

            if (pollEntries.Count == 0)
                return;

            var descriptors = pollEntries.ToArray();
            var result = LibztNative.Poll(
                ref descriptors[0], checked((uint)descriptors.Length), 0);
            Interlocked.Increment(ref _pollCalls);
            Interlocked.Add(ref _fdObservations, descriptors.Length);

            if (result < 0)
            {
                for (var i = 0; i < descriptors.Length; i++)
                {
                    var error = LibztNative.GetLastSocketError(descriptors[i].Fd);
                    foreach (var request in memberships[i])
                        request.Completion.TrySetException(
                            new LibztException("zts_bsd_poll(shared)", result, error));
                }
                return;
            }

            if (result == 0)
                return;

            for (var i = 0; i < descriptors.Length; i++)
            {
                var revents = descriptors[i].Revents;
                if (revents == 0)
                    continue;

                var socketError = (revents
                    & (LibztNative.PollError | LibztNative.PollInvalid)) != 0
                    ? LibztNative.GetLastSocketError(descriptors[i].Fd)
                    : 0;

                foreach (var request in memberships[i])
                {
                    if ((revents & request.Events) != 0)
                    {
                        request.Completion.TrySetResult();
                    }
                    else if ((revents
                              & (LibztNative.PollError | LibztNative.PollInvalid)) != 0)
                    {
                        request.Completion.TrySetException(socketError != 0
                            ? new LibztException(
                                "zts_bsd_poll(shared revents)",
                                LibztNative.ErrSocket, socketError)
                            : new IOException(
                                $"libzt shared poll failed on fd {request.Fd}: revents=0x{revents:x}"));
                    }
                }
            }
        }
        finally
        {
            for (var index = gates.Count - 1; index >= 0; index--)
                Monitor.Exit(gates[index]);
        }
    }

    private sealed class WaitRequest(
        int fd, short events, CancellationToken token, object nativeGate,
        Func<int> currentFd, string disposedObjectName)
    {
        internal int Fd { get; } = fd;
        internal short Events { get; } = events;
        internal CancellationToken Token { get; } = token;
        internal object NativeGate { get; } = nativeGate;
        internal Func<int> CurrentFd { get; } = currentFd;
        internal string DisposedObjectName { get; } = disposedObjectName;
        internal TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
