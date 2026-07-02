using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WgSplitDns.Services;

public record PeerStats(byte[] PublicKey, ulong TxBytes, ulong RxBytes, DateTime? LastHandshakeUtc);

// Thin wrapper over the official signed wireguard.dll (WireGuardNT). Struct offsets
// mirror wireguard.h, where all three structs are __declspec(align(8)).
public sealed class WireGuardAdapter : IDisposable
{
    const string Pool = "WgSplitDns";

    // WIREGUARD_INTERFACE: Flags u32@0, ListenPort u16@4, PrivateKey[32]@6, PublicKey[32]@38, PeersCount u32@72, size 80
    const int IfaceSize = 80;
    // WIREGUARD_PEER: Flags u32@0, Reserved u32@4, PublicKey[32]@8, PresharedKey[32]@40, Keepalive u16@72,
    //                 Endpoint SOCKADDR_INET@76(28), Tx u64@104, Rx u64@112, LastHandshake u64@120, AllowedIPsCount u32@128, size 136
    const int PeerSize = 136;
    // WIREGUARD_ALLOWED_IP: Address[16]@0, AddressFamily u16@16, Cidr u8@18, size 24
    const int AllowedIpSize = 24;

    const uint IfaceHasPrivateKey = 1 << 1;
    const uint IfaceReplacePeers = 1 << 3;
    const uint PeerHasPublicKey = 1 << 0;
    const uint PeerHasPresharedKey = 1 << 1;
    const uint PeerHasKeepalive = 1 << 2;
    const uint PeerHasEndpoint = 1 << 3;
    const uint PeerReplaceAllowedIps = 1 << 5;

    [DllImport("wireguard", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WireGuardCreateAdapter(string pool, string name, IntPtr requestedGuid);

    [DllImport("wireguard")]
    static extern void WireGuardCloseAdapter(IntPtr adapter);

    [DllImport("wireguard", SetLastError = true)]
    static extern bool WireGuardSetConfiguration(IntPtr adapter, byte[] config, uint bytes);

    [DllImport("wireguard", SetLastError = true)]
    static extern bool WireGuardGetConfiguration(IntPtr adapter, byte[] config, ref uint bytes);

    [DllImport("wireguard", SetLastError = true)]
    static extern bool WireGuardSetAdapterState(IntPtr adapter, int state);

    [DllImport("wireguard")]
    static extern void WireGuardGetAdapterLUID(IntPtr adapter, out ulong luid);

    IntPtr _handle;

    public ulong Luid { get; private set; }

    public static WireGuardAdapter Create(string name)
    {
        var handle = WireGuardCreateAdapter(Pool, name, IntPtr.Zero);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Adapter creation failed (win32 {Marshal.GetLastWin32Error()}). Is wireguard.dll beside the exe?");
        var adapter = new WireGuardAdapter { _handle = handle };
        WireGuardGetAdapterLUID(handle, out var luid);
        adapter.Luid = luid;
        return adapter;
    }

    public void SetConfiguration(byte[] privateKey, IReadOnlyList<(byte[] PublicKey, byte[]? Psk, IPEndPoint? Endpoint, IReadOnlyList<(IPAddress Ip, byte Cidr)> AllowedIps, ushort Keepalive)> peers)
    {
        int size = IfaceSize + peers.Sum(p => PeerSize + p.AllowedIps.Count * AllowedIpSize);
        var buf = new byte[size];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, IfaceHasPrivateKey | IfaceReplacePeers);
        privateKey.CopyTo(span[6..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[72..], (uint)peers.Count);
        int off = IfaceSize;
        foreach (var p in peers)
        {
            uint flags = PeerHasPublicKey | PeerReplaceAllowedIps;
            if (p.Psk is not null) flags |= PeerHasPresharedKey;
            if (p.Endpoint is not null) flags |= PeerHasEndpoint;
            if (p.Keepalive > 0) flags |= PeerHasKeepalive;
            BinaryPrimitives.WriteUInt32LittleEndian(span[off..], flags);
            p.PublicKey.CopyTo(span[(off + 8)..]);
            p.Psk?.CopyTo(span[(off + 40)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(off + 72)..], p.Keepalive);
            if (p.Endpoint is not null)
                WriteSockaddrInet(span[(off + 76)..], p.Endpoint);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(off + 128)..], (uint)p.AllowedIps.Count);
            off += PeerSize;
            foreach (var (ip, cidr) in p.AllowedIps)
            {
                var bytes = ip.GetAddressBytes();
                bytes.CopyTo(span[off..]);
                BinaryPrimitives.WriteUInt16LittleEndian(span[(off + 16)..],
                    (ushort)(ip.AddressFamily == AddressFamily.InterNetwork ? 2 : 23));
                span[off + 18] = cidr;
                off += AllowedIpSize;
            }
        }
        if (!WireGuardSetConfiguration(_handle, buf, (uint)size))
            throw new InvalidOperationException($"SetConfiguration failed (win32 {Marshal.GetLastWin32Error()}).");
    }

    public void SetState(bool up)
    {
        if (!WireGuardSetAdapterState(_handle, up ? 1 : 0))
            throw new InvalidOperationException($"SetAdapterState failed (win32 {Marshal.GetLastWin32Error()}).");
    }

    public List<PeerStats> GetStats()
    {
        uint bytes = 4096;
        byte[] buf;
        while (true)
        {
            buf = new byte[bytes];
            if (WireGuardGetConfiguration(_handle, buf, ref bytes)) break;
            if (Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */)
                return new List<PeerStats>();
        }
        var span = buf.AsSpan();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[72..]);
        var result = new List<PeerStats>((int)count);
        int off = IfaceSize;
        for (uint i = 0; i < count; i++)
        {
            var pub = span.Slice(off + 8, 32).ToArray();
            var tx = BinaryPrimitives.ReadUInt64LittleEndian(span[(off + 104)..]);
            var rx = BinaryPrimitives.ReadUInt64LittleEndian(span[(off + 112)..]);
            var hs = BinaryPrimitives.ReadUInt64LittleEndian(span[(off + 120)..]);
            var allowed = BinaryPrimitives.ReadUInt32LittleEndian(span[(off + 128)..]);
            result.Add(new PeerStats(pub, tx, rx, hs == 0 ? null : DateTime.FromFileTimeUtc((long)hs)));
            off += PeerSize + (int)allowed * AllowedIpSize;
        }
        return result;
    }

    static void WriteSockaddrInet(Span<byte> dst, IPEndPoint ep)
    {
        if (ep.AddressFamily == AddressFamily.InterNetwork)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dst, 2);
            BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)ep.Port);
            ep.Address.GetAddressBytes().CopyTo(dst[4..]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dst, 23);
            BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)ep.Port);
            ep.Address.GetAddressBytes().CopyTo(dst[8..]);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            WireGuardCloseAdapter(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
