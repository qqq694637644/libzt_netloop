namespace NetLoop.Core;

public sealed class NetworkEpoch
{
    private long _value;

    public long Value => Interlocked.Read(ref _value);

    public long Advance() => Interlocked.Increment(ref _value);
}
