using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetLoop.Host;

/// <summary>
/// Passive snapshot of Windows' effective public IPv4/IPv6 routes. Network
/// callbacks are only wakeups: the OS-selected egress and its gateway/source
/// addresses determine whether this is a different physical connection.
/// No probe packets, shell commands or artificial delays are introduced.
/// </summary>
internal sealed record PhysicalEgressIdentity(
    string InterfaceId,
    string IPv4Source,
    string IPv4Gateway,
    string IPv6Prefix,
    string WifiBssid = "")
{
    internal static PhysicalEgressIdentity CaptureFailed { get; } =
        new("__capture_failed__", "", "", "");
    internal bool IsCaptureFailed => InterfaceId == "__capture_failed__";

    internal bool DiffersFrom(PhysicalEgressIdentity other)
    {
        if (InterfaceId != other.InterfaceId)
            return true;
        if (!string.IsNullOrEmpty(WifiBssid)
            && !string.IsNullOrEmpty(other.WifiBssid)
            && WifiBssid != other.WifiBssid)
            return true;
        if (!string.IsNullOrEmpty(IPv4Source)
            && !string.IsNullOrEmpty(other.IPv4Source)
            && IPv4Source != other.IPv4Source)
            return true;
        if (!string.IsNullOrEmpty(IPv4Gateway)
            && !string.IsNullOrEmpty(other.IPv4Gateway)
            && IPv4Gateway != other.IPv4Gateway)
            return true;
        // DAD can add/remove IPv6 during one handover. Compare stable
        // /64 prefixes only once both snapshots have a global prefix.
        return !string.IsNullOrEmpty(IPv6Prefix)
            && !string.IsNullOrEmpty(other.IPv6Prefix)
            && IPv6Prefix != other.IPv6Prefix;
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
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var nic = interfaces.FirstOrDefault(candidate =>
                candidate.OperationalStatus == OperationalStatus.Up
                && candidate.NetworkInterfaceType is not (
                    NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && candidate.GetIPProperties().GetIPv4Properties()?.Index == v4Index)
                ?? interfaces.FirstOrDefault(candidate =>
                    candidate.OperationalStatus == OperationalStatus.Up
                    && candidate.NetworkInterfaceType is not (
                        NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && candidate.GetIPProperties().GetIPv6Properties()?.Index == v6Index);
            if (nic is null)
                return null;

            var properties = nic.GetIPProperties();
            var gatewayV4 = properties.GatewayAddresses
                .Select(g => g.Address)
                .Where(g => g.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.Any.Equals(g))
                .Select(g => g.ToString())
                .OrderBy(g => g, StringComparer.Ordinal)
                .FirstOrDefault() ?? "";
            var sourceV4 = properties.UnicastAddresses
                .Select(u => u.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork
                             && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString())
                .OrderBy(ip => ip, StringComparer.Ordinal)
                .FirstOrDefault() ?? "";
            var ipv6Prefix = properties.UnicastAddresses
                .Select(u => u.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6
                             && !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal
                             && !IPAddress.IsLoopback(ip))
                .Select(ip => Convert.ToHexString(ip.GetAddressBytes().AsSpan(0, 8)))
                .OrderBy(ip => ip, StringComparer.Ordinal)
                .FirstOrDefault() ?? "";
            return new PhysicalEgressIdentity(
                nic.Id, sourceV4, gatewayV4, ipv6Prefix, WirelessBssid(nic));
        }
        catch (Exception ex) when (
            ex is NetworkInformationException or SocketException
                or InvalidOperationException or ArgumentException)
        {
            // A transient IP Helper error is not evidence of an actual
            // disconnect. Native still refreshes Binder on the notification.
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
        return GetBestInterfaceEx(address, out var index) == 0
            ? checked((int)index) : -1;
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
                // WLAN_CONNECTION_ATTRIBUTES: state(4), mode(4), profile(512),
                // DOT11_SSID(36), BSS type(4), then DOT11_BSSID(6).
                if (size < 566)
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
