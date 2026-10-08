using System.Net;

namespace NetLoop.Core;

public readonly record struct ProxyTarget(string Host, ushort Port)
{
    public bool TryGetIPAddress(out IPAddress address) => IPAddress.TryParse(Host, out address!);

    public override string ToString()
        => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
