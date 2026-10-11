using System.Net;
using NetLoop.Core;

namespace NetLoop.Host;

/// <summary>
/// Optional inbound Agent access restrictions. These rules never select a
/// route or identify a ZeroTier node cryptographically: TCP/UDP source IPs
/// must already have passed through the authenticated ZeroTier network.
/// </summary>
internal sealed class AgentAccessPolicy
{
    internal readonly record struct PortRange(ushort From, ushort To)
    {
        internal bool Contains(ushort port) => port >= From && port <= To;

        public override string ToString()
            => From == To ? From.ToString() : $"{From}-{To}";
    }

    private readonly OverlayPrefix[] _sourcePrefixes;
    private readonly PortRange[] _targetPorts;
    private long _rejectedSources;
    private long _rejectedTargets;

    internal AgentAccessPolicy(
        IEnumerable<OverlayPrefix> sourcePrefixes,
        IEnumerable<PortRange> targetPorts)
    {
        _sourcePrefixes = sourcePrefixes.Distinct().ToArray();
        _targetPorts = targetPorts.Distinct().ToArray();
    }

    internal bool RestrictSources => _sourcePrefixes.Length != 0;
    internal bool RestrictTargets => _targetPorts.Length != 0;
    internal long RejectedSources => Interlocked.Read(ref _rejectedSources);
    internal long RejectedTargets => Interlocked.Read(ref _rejectedTargets);

    internal bool AllowsSource(IPAddress? source)
    {
        if (!RestrictSources)
            return true;

        if (source is not null && _sourcePrefixes.Any(prefix => prefix.Contains(source)))
            return true;

        Interlocked.Increment(ref _rejectedSources);
        return false;
    }

    internal bool AllowsTarget(ProxyTarget target)
    {
        if (!RestrictTargets || _targetPorts.Any(range => range.Contains(target.Port)))
            return true;

        Interlocked.Increment(ref _rejectedTargets);
        return false;
    }

    internal static OverlayPrefix ParseSource(string text)
    {
        var input = text.Trim();
        if (!input.Contains('/'))
        {
            if (!IPAddress.TryParse(input, out var address))
                throw new ArgumentException($"Invalid --agent-allow-source IP: {text}");
            return new OverlayPrefix(address, address.GetAddressBytes().Length * 8);
        }

        try
        {
            return OverlayPrefix.Parse(input);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException($"Invalid --agent-allow-source CIDR: {text}", ex);
        }
    }

    internal static PortRange ParsePortRange(string text)
    {
        var parts = text.Split('-', 2);
        if (parts.Length is < 1 or > 2
            || !ushort.TryParse(parts[0], out var start)
            || start == 0
            || (parts.Length == 2
                && (!ushort.TryParse(parts[1], out var end) || end < start)))
        {
            throw new ArgumentException(
                $"Invalid --agent-allow-target-port: {text} (expected PORT or START-END).");
        }

        return new PortRange(start, parts.Length == 1 ? start : ushort.Parse(parts[1]));
    }
}
