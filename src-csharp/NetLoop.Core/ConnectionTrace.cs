namespace NetLoop.Core;

/// <summary>
/// Process-unique diagnostic IDs and async-flow scopes. These IDs describe
/// individual local connections; they are not transmitted on the wire.
/// </summary>
public static class ConnectionTrace
{
    private static readonly string Prefix = Guid.NewGuid().ToString("N");
    private static readonly AsyncLocal<string?> Inbound = new();
    private static readonly AsyncLocal<string?> PeerAttempt = new();
    private static long _nextId;

    public static string NewId()
        => $"{Prefix}-{Interlocked.Increment(ref _nextId):x}";

    public static string? InboundConnectionId => Inbound.Value;

    public static string? PeerAttemptId => PeerAttempt.Value;

    public static IDisposable EnterInbound(string connectionId)
        => new Scope(Inbound, connectionId);

    public static IDisposable EnterPeerAttempt(string attemptId)
        => new Scope(PeerAttempt, attemptId);

    private sealed class Scope : IDisposable
    {
        private readonly AsyncLocal<string?> _slot;
        private readonly string? _previous;

        internal Scope(AsyncLocal<string?> slot, string value)
        {
            _slot = slot;
            _previous = slot.Value;
            slot.Value = value;
        }

        public void Dispose()
            => _slot.Value = _previous;
    }
}

/// <summary>Stable local ID shared by TCP accept and SOCKS5 processing.</summary>
public interface IConnectionTrace
{
    string ConnectionId { get; }
}
