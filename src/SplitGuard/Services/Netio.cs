using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SplitGuard.Services;

// iphlpapi calls for assigning interface addresses and routes. Structs are handled as
// explicit-layout blobs matching the Windows SDK; the adapter's teardown removes both,
// so there is no delete bookkeeping.
public static class Netio
{
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    struct MibUnicastRow
    {
        [FieldOffset(0)] public SockaddrInet Address;
        [FieldOffset(32)] public ulong InterfaceLuid;
        [FieldOffset(40)] public uint InterfaceIndex;
        [FieldOffset(44)] public uint PrefixOrigin;
        [FieldOffset(48)] public uint SuffixOrigin;
        [FieldOffset(52)] public uint ValidLifetime;
        [FieldOffset(56)] public uint PreferredLifetime;
        [FieldOffset(60)] public byte OnLinkPrefixLength;
        [FieldOffset(61)] public byte SkipAsSource;
        [FieldOffset(64)] public uint DadState;
        [FieldOffset(68)] public uint ScopeId;
        [FieldOffset(72)] public long CreationTimeStamp;
    }

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    struct MibForwardRow
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public SockaddrInet DestinationPrefix;
        [FieldOffset(40)] public byte DestinationPrefixLength;
        [FieldOffset(44)] public SockaddrInet NextHop;
        [FieldOffset(72)] public byte SitePrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(80)] public uint PreferredLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public uint Protocol;
        [FieldOffset(92)] public byte Loopback;
        [FieldOffset(93)] public byte AutoconfigureAddress;
        [FieldOffset(94)] public byte Publish;
        [FieldOffset(95)] public byte Immortal;
        [FieldOffset(96)] public uint Age;
        [FieldOffset(100)] public uint Origin;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public unsafe struct SockaddrInet
    {
        [FieldOffset(0)] public fixed byte Raw[28];

        public static SockaddrInet From(IPAddress ip)
        {
            var s = new SockaddrInet();
            var bytes = ip.GetAddressBytes();
            unsafe
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    s.Raw[0] = 2;
                    for (int i = 0; i < 4; i++) s.Raw[4 + i] = bytes[i];
                }
                else
                {
                    s.Raw[0] = 23;
                    for (int i = 0; i < 16; i++) s.Raw[8 + i] = bytes[i];
                }
            }
            return s;
        }

        public IPAddress ToIpAddress()
        {
            unsafe
            {
                fixed (byte* p = Raw)
                {
                    var span = new ReadOnlySpan<byte>(p, 28);
                    return span[0] == 2
                        ? new IPAddress(span.Slice(4, 4))
                        : new IPAddress(span.Slice(8, 16));
                }
            }
        }
    }

    [DllImport("iphlpapi")] static extern void InitializeUnicastIpAddressEntry(out MibUnicastRow row);
    [DllImport("iphlpapi")] static extern uint CreateUnicastIpAddressEntry(ref MibUnicastRow row);
    [DllImport("iphlpapi")] static extern void InitializeIpForwardEntry(out MibForwardRow row);
    [DllImport("iphlpapi")] static extern uint CreateIpForwardEntry2(ref MibForwardRow row);

    [DllImport("iphlpapi")]
    static extern uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress,
        ref SockaddrInet destination, uint options, out MibForwardRow bestRoute, out SockaddrInet bestSource);

    [DllImport("iphlpapi")] static extern uint GetIpForwardEntry2(ref MibForwardRow row);
    [DllImport("iphlpapi")] static extern uint SetIpForwardEntry2(ref MibForwardRow row);

    public static void AddAddress(ulong luid, IPAddress ip, byte prefixLength)
    {
        InitializeUnicastIpAddressEntry(out var row);
        row.Address = SockaddrInet.From(ip);
        row.InterfaceLuid = luid;
        row.OnLinkPrefixLength = prefixLength;
        row.DadState = 4; // IpDadStatePreferred: skip duplicate address detection on a point-to-point link
        var err = CreateUnicastIpAddressEntry(ref row);
        if (err != 0 && err != 5010 /* ERROR_OBJECT_ALREADY_EXISTS */)
            throw new InvalidOperationException($"Assigning {ip}/{prefixLength} failed (win32 {err}).");
    }

    public static void AddRoute(ulong luid, IPAddress destination, byte prefixLength, IPAddress? nextHop = null, uint metric = 0)
    {
        // CreateIpForwardEntry2 rejects prefixes with host bits set (ERROR_INVALID_PARAMETER).
        destination = MaskToNetwork(destination, prefixLength);
        InitializeIpForwardEntry(out var row);
        row.InterfaceLuid = luid;
        row.DestinationPrefix = SockaddrInet.From(destination);
        row.DestinationPrefixLength = prefixLength;
        row.NextHop = SockaddrInet.From(nextHop ?? (destination.AddressFamily == AddressFamily.InterNetwork
            ? IPAddress.Any : IPAddress.IPv6Any));
        row.Metric = metric;
        var err = CreateIpForwardEntry2(ref row);
        if (err != 0 && err != 5010)
            throw new InvalidOperationException($"Route {destination}/{prefixLength} failed (win32 {err}).");
    }

    // Retarget an existing route's metric in place (failover arbitration between tunnels
    // sharing the same destination prefix). Missing routes are ignored — the adapter may
    // already be tearing down.
    public static void SetRouteMetric(ulong luid, IPAddress destination, byte prefixLength, uint metric)
    {
        destination = MaskToNetwork(destination, prefixLength);
        InitializeIpForwardEntry(out var row);
        row.InterfaceLuid = luid;
        row.DestinationPrefix = SockaddrInet.From(destination);
        row.DestinationPrefixLength = prefixLength;
        row.NextHop = SockaddrInet.From(destination.AddressFamily == AddressFamily.InterNetwork
            ? IPAddress.Any : IPAddress.IPv6Any);
        if (GetIpForwardEntry2(ref row) != 0) return;
        if (row.Metric == metric) return;
        row.Metric = metric;
        SetIpForwardEntry2(ref row);
    }

    static IPAddress MaskToNetwork(IPAddress ip, byte prefixLength)
    {
        var bytes = ip.GetAddressBytes();
        int bits = prefixLength;
        for (int i = 0; i < bytes.Length; i++, bits -= 8)
        {
            if (bits >= 8) continue;
            bytes[i] = bits <= 0 ? (byte)0 : (byte)(bytes[i] & (0xFF << (8 - bits)));
        }
        return new IPAddress(bytes);
    }

    // Host route for the tunnel endpoint via the current best (physical) route, so
    // full-tunnel configs don't loop tunnel traffic into the tunnel.
    public static void AddEndpointHostRoute(IPAddress endpoint)
    {
        var dest = SockaddrInet.From(endpoint);
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, ref dest, 0, out var best, out _) != 0)
            return; // no current route to the endpoint: nothing to pin
        InitializeIpForwardEntry(out var row);
        row.InterfaceLuid = best.InterfaceLuid;
        row.InterfaceIndex = best.InterfaceIndex;
        row.DestinationPrefix = dest;
        row.DestinationPrefixLength = endpoint.AddressFamily == AddressFamily.InterNetwork ? (byte)32 : (byte)128;
        row.NextHop = best.NextHop;
        row.Metric = 0;
        var err = CreateIpForwardEntry2(ref row);
        if (err != 0 && err != 5010)
            throw new InvalidOperationException($"Endpoint host route failed (win32 {err}).");
    }
}
