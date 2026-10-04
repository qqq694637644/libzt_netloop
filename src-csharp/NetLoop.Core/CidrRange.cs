using System.Net;
using System.Net.Sockets;

namespace NetLoop.Core;

public readonly record struct CidrRange(IPAddress Network, int PrefixLength)
{
    public static CidrRange Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        var address = IPAddress.Parse(parts[0]);
        var maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : maxBits;
        if (prefix is < 0 || prefix > maxBits)
            throw new FormatException($"Invalid CIDR prefix: {value}");

        return new CidrRange(address, prefix);
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Network.AddressFamily)
            return false;

        var left = Network.GetAddressBytes();
        var right = address.GetAddressBytes();
        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (left[fullBytes] & mask) == (right[fullBytes] & mask);
    }

    public override string ToString() => $"{Network}/{PrefixLength}";
}

public sealed record ManagedRoute(CidrRange Target, IPAddress Via, ushort Flags, ushort Metric)
{
    public bool IsDirect => Via.Equals(IPAddress.Any) || Via.Equals(IPAddress.IPv6Any);
}
