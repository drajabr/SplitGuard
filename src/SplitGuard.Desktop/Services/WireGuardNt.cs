using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SplitGuard.Services;

public record PeerStats(byte[] PublicKey, ulong TxBytes, ulong RxBytes, DateTime? LastHandshakeUtc);

// Thin wrapper over the official signed wireguard.dll (WireGuardNT). Struct offsets
// mirror wireguard.h, where all three structs are __declspec(align(8)).
public sealed class WireGuardAdapter : IDisposable
{
    const string TunnelType = "SplitGuard";

    // WIREGUARD_INTERFACE: Flags u32@0, ListenPort u16@4, PrivateKey[32]@6, PublicKey[32]@38, PeersCount u32@72, size 80
    const int IfaceSize = 80;
    // WIREGUARD_PEER: Flags u32@0, Reserved u32@4, PublicKey[32]@8, PresharedKey[32]@40, Keepalive u16@72,
    //                 Endpoint SOCKADDR_INET@76(28), Tx u64@104, Rx u64@112, LastHandshake u64@120, AllowedIPsCount u32@128, size 136
    const int PeerSize = 136;
    // WIREGUARD_ALLOWED_IP: Address[16]@0, AddressFamily u16@16, Cidr u8@18, size 24
    const int AllowedIpSize = 24;

    const uint IfaceHasPrivateKey = 1 << 1;
    const uint IfaceHasListenPort = 1 << 2;
    const uint IfaceReplacePeers = 1 << 3;
    const uint PeerHasPublicKey = 1 << 0;
    const uint PeerHasPresharedKey = 1 << 1;
    const uint PeerHasKeepalive = 1 << 2;
    const uint PeerHasEndpoint = 1 << 3;
    const uint PeerReplaceAllowedIps = 1 << 5;
    const uint PeerUpdateOnly = 1 << 7; // WIREGUARD_PEER_UPDATE: never create, only patch

    [DllImport("wireguard", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WireGuardCreateAdapter(string name, string tunnelType, in Guid requestedGuid);

    [DllImport("wireguard", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WireGuardOpenAdapter(string name);

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
        // A DETERMINISTIC GUID per tunnel name (not the driver's per-creation random one):
        // Windows registers the friendly name against the interface GUID, so a random GUID
        // meant every reconnect competed with its own dead registration and came up as
        // "name 2", "name 3", … — and NetworkList profiles piled up the same way. A stable
        // GUID makes each reconnect the SAME interface: same name, same network profile.
        // (wireguard-windows does exactly this, hashing the tunnel name.)
        var guid = DeterministicGuid(name);
        // A crash-orphaned adapter can still own this name/GUID: the driver only collects
        // orphans in an ASYNC sweep queued by any Create/Open/Close call, so a failed
        // create pokes the sweep (open+close both queue it) and retries with backoff
        // instead of failing the connect outright.
        IntPtr handle;
        for (int attempt = 0; ; attempt++)
        {
            handle = WireGuardCreateAdapter(name, TunnelType, in guid);
            if (handle != IntPtr.Zero) break;
            var err = Marshal.GetLastWin32Error();
            if (attempt >= 4)
                throw new InvalidOperationException($"Adapter creation failed (win32 {err}). Is wireguard.dll beside the exe?");
            try { var orphan = WireGuardOpenAdapter(name); if (orphan != IntPtr.Zero) WireGuardCloseAdapter(orphan); }
            catch { }
            Thread.Sleep(250 * (attempt + 1));
        }
        var adapter = new WireGuardAdapter { _handle = handle };
        WireGuardGetAdapterLUID(handle, out var luid);
        adapter.Luid = luid;
        return adapter;
    }

    static Guid DeterministicGuid(string name)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("SplitGuard adapter\n" + name));
        var bytes = hash[..16];
        // Stamp RFC 4122 version/variant bits so the GUID is well-formed (cosmetic; the
        // driver accepts any value, but tools that parse it expect a valid v4 layout).
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public void SetConfiguration(byte[] privateKey, ushort listenPort, IReadOnlyList<(byte[] PublicKey, byte[]? Psk, IPEndPoint? Endpoint, IReadOnlyList<(IPAddress Ip, byte Cidr)> AllowedIps, ushort Keepalive)> peers)
    {
        int size = IfaceSize + peers.Sum(p => PeerSize + p.AllowedIps.Count * AllowedIpSize);
        var buf = new byte[size];
        var span = buf.AsSpan();
        var ifaceFlags = IfaceHasPrivateKey | IfaceReplacePeers;
        if (listenPort > 0) ifaceFlags |= IfaceHasListenPort;
        BinaryPrimitives.WriteUInt32LittleEndian(span, ifaceFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], listenPort);
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
        lock (_hLock)
        {
            if (_handle == IntPtr.Zero) return; // disposed mid-reconcile: nothing to configure
            if (!WireGuardSetConfiguration(_handle, buf, (uint)size))
                throw new InvalidOperationException($"SetConfiguration failed (win32 {Marshal.GetLastWin32Error()}).");
        }
    }

    // Patch ONE peer's endpoint in place (matched by public key): no interface fields, no
    // peer replacement — roamed endpoints and handshake state of every peer survive. Used
    // by the stale-endpoint DNS re-resolution.
    public void UpdatePeerEndpoint(byte[] publicKey, IPEndPoint endpoint)
    {
        int size = IfaceSize + PeerSize;
        var buf = new byte[size];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0);        // no interface flags
        BinaryPrimitives.WriteUInt32LittleEndian(span[72..], 1); // one peer entry
        int off = IfaceSize;
        BinaryPrimitives.WriteUInt32LittleEndian(span[off..], PeerHasPublicKey | PeerHasEndpoint | PeerUpdateOnly);
        publicKey.CopyTo(span[(off + 8)..]);
        WriteSockaddrInet(span[(off + 76)..], endpoint);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(off + 128)..], 0); // untouched allowed IPs
        lock (_hLock)
        {
            if (_handle == IntPtr.Zero) return;
            if (!WireGuardSetConfiguration(_handle, buf, (uint)size))
                throw new InvalidOperationException($"UpdatePeerEndpoint failed (win32 {Marshal.GetLastWin32Error()}).");
        }
    }

    public void SetState(bool up)
    {
        lock (_hLock)
        {
            if (_handle == IntPtr.Zero) return;
            if (!WireGuardSetAdapterState(_handle, up ? 1 : 0))
                throw new InvalidOperationException($"SetAdapterState failed (win32 {Marshal.GetLastWin32Error()}).");
        }
    }

    public List<PeerStats> GetStats()
    {
        uint bytes = 4096;
        byte[] buf;
        lock (_hLock)
        {
            if (_handle == IntPtr.Zero) return new List<PeerStats>();
            while (true)
            {
                buf = new byte[bytes];
                if (WireGuardGetConfiguration(_handle, buf, ref bytes)) break;
                if (Marshal.GetLastWin32Error() != 234 /* ERROR_MORE_DATA */)
                    return new List<PeerStats>();
            }
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

    // Guards _handle across Dispose vs the poll thread's GetStats / a reconcile's
    // SetConfiguration — a disposed-then-recycled handle value could otherwise read or
    // WRITE configuration on the wrong adapter.
    readonly object _hLock = new();

    public void Dispose()
    {
        lock (_hLock)
        {
            if (_handle != IntPtr.Zero)
            {
                WireGuardCloseAdapter(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}

// Removes NetworkList profiles named "<name>" or "<name> N" (and their Signatures
// entries) before an adapter is (re)created — leftovers from the random-GUID era made
// NLA name the fresh profile with the next free suffix. Only exact name matches are
// touched; purely cosmetic, so every failure is swallowed.
public static class NetworkProfiles
{
    public static void SweepStale(string name)
    {
        try
        {
            using var profiles = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles", writable: true);
            if (profiles is null) return;
            var pattern = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(name) + @"( \d+)?$");
            var doomed = new List<string>();
            foreach (var sub in profiles.GetSubKeyNames())
            {
                using var k = profiles.OpenSubKey(sub);
                if (k?.GetValue("ProfileName") is string pn && pattern.IsMatch(pn))
                    doomed.Add(sub);
            }
            foreach (var sub in doomed)
            {
                try { profiles.DeleteSubKeyTree(sub); } catch { }
            }
            if (doomed.Count == 0) return;
            using var sigs = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\Unmanaged", writable: true);
            if (sigs is null) return;
            foreach (var sub in sigs.GetSubKeyNames())
            {
                bool stale;
                using (var k = sigs.OpenSubKey(sub))
                    stale = k?.GetValue("ProfileGuid") is string pg && doomed.Contains(pg);
                if (stale) { try { sigs.DeleteSubKeyTree(sub); } catch { } }
            }
        }
        catch { }
    }
}
