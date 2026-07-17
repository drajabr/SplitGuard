using Android.OS;
using Android.Systems;
using Java.IO;

namespace SplitGuard.Droid;

// Splices between the real tun fd (from VpnService.establish) and wireguard-go: all
// packets pass through untouched EXCEPT IPv4/UDP to the virtual DNS IP, which are
// answered by the DnsForwarder and written straight back to the tun. wireguard-go
// reads/writes one full IP packet per syscall, so a SOCK_DGRAM unix socketpair
// preserves the packet framing it expects.
class TunPacketRelay : IDisposable
{
    // Xamarin's GC will collect a ParcelFileDescriptor with no managed roots and its
    // finalizer closes the underlying fd — which silently tore the tunnel down ~6s in.
    // The relay owns RAW fds (detached from any PFD) and this static root keeps the live
    // relay itself alive for the tunnel's lifetime.
    static TunPacketRelay? _live;

    readonly ParcelFileDescriptor _tunPfd;     // VpnService tun; kept alive via _live root
    readonly Java.IO.FileDescriptor _tunFd;
    readonly Java.IO.FileDescriptor _ourEnd;   // relay side of the socketpair
    readonly int _wgRawFd;                      // wireguard-go side (handed off via WgEndFd)

    readonly DnsForwarder _dns;
    readonly byte[] _dnsIp;
    readonly object _tunWriteGate = new();
    volatile bool _running = true;
    Thread? _up, _down;

    // wireguard-go's tun-side fd. Call once — ownership transfers to wgTurnOn.
    public int WgEndFd => _wgRawFd;

    public TunPacketRelay(ParcelFileDescriptor tun, DnsForwarder dns)
    {
        _dns = dns;
        _dnsIp = System.Net.IPAddress.Parse(DnsForwarder.VirtualDns).GetAddressBytes();
        // Hold the tun PFD (don't detach — that resets its FileDescriptor to -1); the static
        // _live root below keeps it — and thus the tun fd — alive against GC finalization,
        // which was silently closing tun0 ~6s in.
        _tunPfd = tun;
        _tunFd = tun.FileDescriptor!;
        var fd0 = new Java.IO.FileDescriptor();
        var fd1 = new Java.IO.FileDescriptor();
        Os.Socketpair(OsConstants.AfUnix, OsConstants.SockDgram, 0, fd0, fd1);
        _ourEnd = fd0;
        // Hand wg-go a raw dup of the other socketpair end; wg-go closes it on turnOff.
        _wgRawFd = ParcelFileDescriptor.Dup(fd1)!.DetachFd();
        Os.Close(fd1);
        _live = this;
    }

    public void Start()
    {
        _up = new Thread(() => Pump(_tunFd, _ourEnd, toTun: false, inspectDns: true)) { IsBackground = true, Name = "tun-wg" };
        _down = new Thread(() => Pump(_ourEnd, _tunFd, toTun: true, inspectDns: false)) { IsBackground = true, Name = "wg-tun" };
        _up.Start();
        _down.Start();
    }

    void Pump(Java.IO.FileDescriptor from, Java.IO.FileDescriptor to, bool toTun, bool inspectDns)
    {
        var buf = new byte[65536];
        while (_running)
        {
            int n;
            try { n = Os.Read(from, buf, 0, buf.Length); }
            catch { break; } // fd closed → tunnel going down
            if (n <= 0) continue;

            if (inspectDns && IsDnsQuery(buf, n))
            {
                var packet = new byte[n];
                Array.Copy(buf, packet, n);
                ThreadPool.QueueUserWorkItem(_ => HandleDns(packet));
                continue;
            }
            try
            {
                if (toTun) lock (_tunWriteGate) Os.Write(to, buf, 0, n);
                else Os.Write(to, buf, 0, n);
            }
            catch { break; }
        }
    }

    // IPv4 / UDP / dst == virtual DNS IP / dst port 53, and not a fragment tail.
    bool IsDnsQuery(byte[] p, int n)
    {
        if (n < 28 || (p[0] >> 4) != 4) return false;
        int ihl = (p[0] & 0x0F) * 4;
        if (ihl < 20 || n < ihl + 8) return false;
        if (p[9] != 17) return false; // UDP
        if ((p[6] & 0x1F) != 0 || p[7] != 0) return false; // fragmented — punt (goes to wg, dropped)
        for (int i = 0; i < 4; i++) if (p[16 + i] != _dnsIp[i]) return false;
        return (p[ihl + 2] << 8 | p[ihl + 3]) == 53;
    }

    void HandleDns(byte[] packet)
    {
        try
        {
            int ihl = (packet[0] & 0x0F) * 4;
            var payload = packet[(ihl + 8)..];
            var answer = _dns.Resolve(payload);
            if (answer is null || !_running) return;

            // Synthesize the reply: swap L3 addresses and L4 ports, fresh lengths/checksums.
            var resp = new byte[ihl + 8 + answer.Length];
            Array.Copy(packet, resp, ihl); // copy IP header (options included)
            // swap src/dst IPs
            Array.Copy(packet, 16, resp, 12, 4);
            Array.Copy(packet, 12, resp, 16, 4);
            var totalLen = resp.Length;
            resp[2] = (byte)(totalLen >> 8); resp[3] = (byte)totalLen;
            resp[8] = 64;          // fresh TTL
            resp[10] = 0; resp[11] = 0;
            var ipSum = Checksum(resp, 0, ihl, 0);
            resp[10] = (byte)(ipSum >> 8); resp[11] = (byte)ipSum;
            // UDP: swap ports, set length, checksum over pseudo-header + payload
            resp[ihl + 0] = packet[ihl + 2]; resp[ihl + 1] = packet[ihl + 3];
            resp[ihl + 2] = packet[ihl + 0]; resp[ihl + 3] = packet[ihl + 1];
            var udpLen = 8 + answer.Length;
            resp[ihl + 4] = (byte)(udpLen >> 8); resp[ihl + 5] = (byte)udpLen;
            resp[ihl + 6] = 0; resp[ihl + 7] = 0;
            answer.CopyTo(resp, ihl + 8);
            long pseudo = 0;
            for (int i = 12; i < 20; i += 2) pseudo += (resp[i] << 8) | resp[i + 1];
            pseudo += 17 + udpLen;
            var udpSum = Checksum(resp, ihl, udpLen, pseudo);
            if (udpSum == 0) udpSum = 0xFFFF;
            resp[ihl + 6] = (byte)(udpSum >> 8); resp[ihl + 7] = (byte)udpSum;

            lock (_tunWriteGate) Os.Write(_tunFd, resp, 0, resp.Length);
        }
        catch { }
    }

    static int Checksum(byte[] data, int offset, int length, long seed)
    {
        long sum = seed;
        var end = offset + length;
        for (int i = offset; i + 1 < end; i += 2) sum += (data[i] << 8) | data[i + 1];
        if ((length & 1) != 0) sum += data[end - 1] << 8;
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (int)(~sum & 0xFFFF);
    }

    public void Dispose()
    {
        _running = false;
        if (ReferenceEquals(_live, this)) _live = null;
        try { Os.Close(_ourEnd); } catch { } // unblocks both pump reads
        try { _tunPfd.Close(); } catch { }
    }
}
