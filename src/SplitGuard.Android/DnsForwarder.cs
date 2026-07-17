using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SplitGuard.Droid;

// Per-domain DNS resolution with NRPT semantics, inside the tunnel: queries the OS
// resolver sends to the virtual DNS IP are handed here by the relay; the qname's
// longest matching namespace picks the upstream (a peer's DNS through the tunnel, or
// the underlying network's DNS around it). Answers go back as synthesized packets.
class DnsForwarder
{
    public const string VirtualDns = "10.255.255.53";
    const int UpstreamTimeoutMs = 1500;

    readonly Android.Net.VpnService _vpn;

    public DnsForwarder(Android.Net.VpnService vpn) => _vpn = vpn;

    // Handle one query payload; returns the response payload (or null to drop).
    // Runs on a worker thread per query (relay fires and forgets).
    public byte[]? Resolve(byte[] query)
    {
        var qname = ParseQName(query);
        var (rules, catchAll) = AndroidSplitDnsService.Instance.Snapshot();

        // Longest-namespace match: ".x" matches "x" and anything under it; bare = exact.
        string? server = null; var best = -1;
        if (qname is not null)
            foreach (var (ns, srv) in rules)
            {
                var match = ns.StartsWith('.')
                    ? qname.EndsWith(ns, StringComparison.OrdinalIgnoreCase)
                      || qname.Equals(ns[1..], StringComparison.OrdinalIgnoreCase)
                    : qname.Equals(ns, StringComparison.OrdinalIgnoreCase);
                if (match && ns.Length > best) { best = ns.Length; server = srv; }
            }

        if (server is not null)
        {
            // Peer DNS: an UNPROTECTED socket routes through the VPN → into the tunnel.
            return Ask(server, query, protect: false) ?? null;
        }

        // No rule: pinned→tunnel chain first (unprotected — these are tunnel/pinned
        // servers), then the underlying network's DNS (protected, bypasses the VPN).
        foreach (var s in catchAll)
            if (Ask(s, query, protect: false) is { } viaChain) return viaChain;
        foreach (var s in AndroidDns.UnderlyingServers())
            if (Ask(s, query, protect: true) is { } viaSystem) return viaSystem;
        return null;
    }

    byte[]? Ask(string server, byte[] query, bool protect)
    {
        if (!IPAddress.TryParse(server, out var ip)
            || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (protect) _vpn.Protect((int)sock.Handle);
            sock.ReceiveTimeout = UpstreamTimeoutMs;
            sock.SendTo(query, new IPEndPoint(ip, 53));
            var buf = new byte[1500];
            var n = sock.Receive(buf);
            if (n < 12) return null;
            var resp = buf[..n];
            // Truncated → retry over TCP for the full answer.
            if ((resp[2] & 0x02) != 0 && AskTcp(ip, query, protect) is { } full) return full;
            return resp;
        }
        catch { return null; }
    }

    byte[]? AskTcp(IPAddress ip, byte[] query, bool protect)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (protect) _vpn.Protect((int)sock.Handle);
            sock.ReceiveTimeout = UpstreamTimeoutMs * 2;
            sock.SendTimeout = UpstreamTimeoutMs;
            sock.Connect(new IPEndPoint(ip, 53));
            var framed = new byte[query.Length + 2];
            framed[0] = (byte)(query.Length >> 8);
            framed[1] = (byte)query.Length;
            query.CopyTo(framed, 2);
            sock.Send(framed);
            var hdr = ReadExact(sock, 2);
            if (hdr is null) return null;
            var len = (hdr[0] << 8) | hdr[1];
            if (len is < 12 or > 65000) return null;
            return ReadExact(sock, len);
        }
        catch { return null; }
    }

    static byte[]? ReadExact(Socket s, int len)
    {
        var buf = new byte[len];
        var off = 0;
        while (off < len)
        {
            var n = s.Receive(buf, off, len - off, SocketFlags.None);
            if (n <= 0) return null;
            off += n;
        }
        return buf;
    }

    // First question's qname from a raw DNS message (lowercase, no trailing dot).
    static string? ParseQName(byte[] msg)
    {
        if (msg.Length < 13) return null;
        var sb = new StringBuilder();
        var i = 12;
        while (i < msg.Length)
        {
            int len = msg[i];
            if (len == 0) break;
            if ((len & 0xC0) != 0 || i + 1 + len > msg.Length) return null; // no compression in questions
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(msg, i + 1, len));
            i += 1 + len;
            if (sb.Length > 253) return null;
        }
        return sb.Length == 0 ? null : sb.ToString().ToLowerInvariant();
    }
}
