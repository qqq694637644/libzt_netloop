using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetLoop.Host;

// Keep IPv4 and IPv6 independent: Windows may route Ethernet IPv4 and
// hotspot/Wi-Fi IPv6 through different physical interfaces.
internal sealed record PhysicalRouteIdentity(
    string InterfaceId,
    string Source,
    string Gateway,
    string Prefix,
    string WifiBssid)
{
    internal bool DiffersFrom(PhysicalRouteIdentity other)
        => InterfaceId != other.InterfaceId
           || DifferentKnown(Source, other.Source)
           || DifferentKnown(Gateway, other.Gateway)
           || DifferentKnown(Prefix, other.Prefix)
           || DifferentKnown(WifiBssid, other.WifiBssid);

    private static bool DifferentKnown(string a, string b)
        => a.Length != 0 && b.Length != 0 && !string.Equals(a, b, StringComparison.Ordinal);
}

// Plain snapshots keep route selection testable without IP Helper or real NICs.
internal sealed record PhysicalInterfaceCandidate(
    string Id, int IPv4Index, int IPv6Index,
    string IPv4Source, string IPv4Gateway,
    string IPv6Prefix, string WifiBssid);

/// <summary>
/// Passive Windows physical-egress snapshot. An OS notification is captured at
/// arrival, not after a cancellation-based debounce. Transient API failure is
/// distinct from actual loss of both default routes.
/// </summary>
internal sealed record PhysicalEgressIdentity(
    PhysicalRouteIdentity? IPv4,
    PhysicalRouteIdentity? IPv6,
    bool IsCaptureFailed = false)
{
    internal static PhysicalEgressIdentity CaptureFailed { get; } = new(null, null, true);

    internal bool DiffersFrom(PhysicalEgressIdentity other)
        => FamilyChanged(IPv4, other.IPv4) || FamilyChanged(IPv6, other.IPv6);

    private static bool FamilyChanged(PhysicalRouteIdentity? previous, PhysicalRouteIdentity? current)
        => previous is null ? current is not null
            : current is null || previous.DiffersFrom(current);

    internal static PhysicalEgressIdentity? SelectEffectiveRoutes(
        IEnumerable<PhysicalInterfaceCandidate> interfaces, int v4Index, int v6Index)
    {
        var all = interfaces.ToArray();
        var v4 = v4Index > 0 ? all.FirstOrDefault(i => i.IPv4Index == v4Index) : null;
        var v6 = v6Index > 0 ? all.FirstOrDefault(i => i.IPv6Index == v6Index) : null;
        if (v4 is null && v6 is null)
            return null;

        return new PhysicalEgressIdentity(
            v4 is null ? null : new PhysicalRouteIdentity(
                v4.Id, v4.IPv4Source, v4.IPv4Gateway, "", v4.WifiBssid),
            v6 is null ? null : new PhysicalRouteIdentity(
                v6.Id, "", "", v6.IPv6Prefix, v6.WifiBssid));
    }

    internal static PhysicalEgressIdentity? Capture()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var v4Index = BestInterface(
                IPAddress.Parse("1.1.1.1"), AddressFamily.InterNetwork);
            var v6Index = BestInterface(
                IPAddress.Parse("2606:4700:4700::1111"), AddressFamily.InterNetworkV6);

            var candidates = new List<PhysicalInterfaceCandidate>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                var index4 = props.GetIPv4Properties()?.Index ?? -1;
                var index6 = props.GetIPv6Properties()?.Index ?? -1;
                if ((v4Index <= 0 || index4 != v4Index)
                    && (v6Index <= 0 || index6 != v6Index))
                    continue;

                var ip4 = props.UnicastAddresses
                    .Select(u => u.Address)
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork
                                 && !IPAddress.IsLoopback(ip))
                    .Select(ip => ip.ToString())
                    .OrderBy(ip => ip, StringComparer.Ordinal)
                    .FirstOrDefault() ?? "";
                var gateway4 = props.GatewayAddresses
                    .Select(g => g.Address)
                    .Where(g => g.AddressFamily == AddressFamily.InterNetwork
                                && !IPAddress.Any.Equals(g))
                    .Select(g => g.ToString())
                    .OrderBy(g => g, StringComparer.Ordinal)
                    .FirstOrDefault() ?? "";
                // Stable /64 identity; temporary IPv6 privacy addresses can
                // rotate without indicating a new physical egress.
                var prefix6 = props.UnicastAddresses
                    .Select(u => u.Address)
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6
                                 && !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal
                                 && !IPAddress.IsLoopback(ip))
                    .Select(ip => Convert.ToHexString(ip.GetAddressBytes().AsSpan(0, 8)))
                    .OrderBy(ip => ip, StringComparer.Ordinal)
                    .FirstOrDefault() ?? "";

                candidates.Add(new PhysicalInterfaceCandidate(
                    nic.Id, index4, index6, ip4, gateway4, prefix6, WirelessBssid(nic)));
            }

            var route = SelectEffectiveRoutes(candidates, v4Index, v6Index);
            if (route is not null)
                return route;

            // Zero effective routes while the system reports offline is a
            // known disconnect. If Windows is online but IP Helper has not
            // settled, retry rather than fabricating an offline transition.
            return NetworkInterface.GetIsNetworkAvailable() ? CaptureFailed : null;
        }
        catch (Exception ex) when (ex is NetworkInformationException
            or SocketException or InvalidOperationException or ArgumentException)
        {
            return CaptureFailed;
        }
    }

    private static int BestInterface(IPAddress destination, AddressFamily family)
    {
        var address = new byte[28];
        var familyValue = family == AddressFamily.InterNetwork ? (ushort)2 : (ushort)23;
        address[0] = (byte)familyValue;
        address[1] = (byte)(familyValue >> 8);
        destination.GetAddressBytes().CopyTo(
            address, family == AddressFamily.InterNetwork ? 4 : 8);
        var rc = GetBestInterfaceEx(address, out var index);
        if (rc == 0)
            return checked((int)index);
        // Explicit route-absence conditions; arbitrary IP Helper failures
        // are NOT evidence that the interface disappeared.
        if (rc is 1231 or 1232 or 1168)
            return -1;
        throw new NetworkInformationException(checked((int)rc));
    }

    private static string WirelessBssid(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211
            || !Guid.TryParse(nic.Id, out var interfaceGuid))
            return "";
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0)
            return "";
        try
        {
            const int CurrentConnection = 7;
            if (WlanQueryInterface(handle, ref interfaceGuid, CurrentConnection,
                    IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0)
                return "";
            try
            {
                // WLAN_CONNECTION_ATTRIBUTES state == connected (1).
                // Profile name: 256 wide chars; SSID: 36 bytes; then
                // DOT11_BSS_TYPE (4) and 6-byte DOT11_BSSID at offset 560.
                if (size < 566 || Marshal.ReadInt32(data) != 1)
                    return "";
                return string.Join(":", Enumerable.Range(560, 6)
                    .Select(i => Marshal.ReadByte(data, i).ToString("X2")));
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(
        [In] byte[] destination, out uint interfaceIndex);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion, IntPtr reserved, out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle, ref Guid interfaceGuid, int opcode,
        IntPtr reserved, out uint dataSize, out IntPtr data,
        IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
}
