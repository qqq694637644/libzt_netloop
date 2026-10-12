namespace NetLoop.Core;

/// <summary>
/// One Runtime-wide byte budget shared by UDP receive and forward queues.
/// It prevents a high packet-count cap from multiplying into unbounded memory
/// across otherwise independent associations.
/// </summary>
public sealed class UdpByteBudget(int maxBytes)
{
    private int _used;
    private int _peak;

    public int MaxBytes { get; } = maxBytes > 0
        ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));

    public int UsedBytes => Volatile.Read(ref _used);
    public int PeakBytes => Volatile.Read(ref _peak);

    public bool TryReserve(int bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        while (true)
        {
            var used = Volatile.Read(ref _used);
            if (bytes > MaxBytes - used)
                return false;
            var next = used + bytes;
            if (Interlocked.CompareExchange(ref _used, next, used) != used)
                continue;
            while (true)
            {
                var peak = Volatile.Read(ref _peak);
                if (next <= peak || Interlocked.CompareExchange(
                        ref _peak, next, peak) == peak)
                    return true;
            }
        }
    }

    public void Release(int bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (Interlocked.Add(ref _used, -bytes) < 0)
            throw new InvalidOperationException("UDP queue released more bytes than reserved.");
    }
}
