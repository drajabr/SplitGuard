using System.Net;
using System.Net.Sockets;
using System.Text;
using SplitGuard.Models;
using SplitGuard.Services;

namespace SplitGuard.Droid;

// wireguard-go cross-platform UAPI wire format ("set" operation): key=value lines,
// keys in lowercase hex. This is what wgTurnOn takes and what wgGetConfig returns.
static class Uapi
{
    public static string BuildSettings(TunnelConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("private_key=").Append(Hex(RuleStore.Unprotect(config.PrivateKeyProtected))).Append('\n');
        if (config.ListenPort != 0)
            sb.Append("listen_port=").Append(config.ListenPort).Append('\n');
        sb.Append("replace_peers=true\n");
        foreach (var p in config.Peers)
        {
            if (string.IsNullOrWhiteSpace(p.PublicKey)) continue;
            sb.Append("public_key=").Append(Hex(p.PublicKey)).Append('\n');
            if (!string.IsNullOrEmpty(p.PresharedKeyProtected))
                sb.Append("preshared_key=").Append(Hex(RuleStore.Unprotect(p.PresharedKeyProtected))).Append('\n');
            // One unresolvable endpoint must not abort the whole config — that peer just
            // stays roaming-only (wireguard-go learns its endpoint from an inbound handshake).
            try
            {
                if (ResolveEndpoint(p.Endpoint) is { } ep)
                    sb.Append("endpoint=").Append(ep).Append('\n');
            }
            catch { }
            if (p.PersistentKeepalive != 0)
                sb.Append("persistent_keepalive_interval=").Append(p.PersistentKeepalive).Append('\n');
            sb.Append("replace_allowed_ips=true\n");
            foreach (var cidr in p.AllowedIps)
                if (WireGuardConf.TryParseCidr(cidr, out var net, out var prefix))
                    sb.Append("allowed_ip=").Append(net).Append('/').Append(prefix).Append('\n');
        }
        return sb.ToString();
    }

    static string Hex(string base64Key)
    {
        var bytes = Convert.FromBase64String(base64Key);
        if (bytes.Length != 32) throw new InvalidOperationException("key must be 32 bytes");
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // "host:port" → "ip:port"; wireguard-go's UAPI needs a literal address. IPv4 preferred
    // (the tunnel interception path is v4-only for now).
    static string? ResolveEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0) throw new InvalidOperationException($"endpoint '{endpoint}' needs host:port");
        var host = endpoint[..idx].Trim('[', ']');
        var port = endpoint[(idx + 1)..];
        if (IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{literal}]:{port}" : $"{literal}:{port}";
        var addrs = Dns.GetHostAddresses(host);
        var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? addrs.FirstOrDefault()
                 ?? throw new InvalidOperationException($"cannot resolve endpoint host '{host}'");
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    }

    // Parse wgGetConfig output into per-peer live stats, keyed by base64 public key.
    public static Dictionary<string, (ulong Tx, ulong Rx, DateTime? Handshake)> ParseStats(string uapi)
    {
        var result = new Dictionary<string, (ulong, ulong, DateTime?)>();
        string? current = null;
        ulong tx = 0, rx = 0; DateTime? hs = null;
        void Flush()
        {
            if (current is not null) result[current] = (tx, rx, hs);
        }
        foreach (var line in uapi.Split('\n'))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx];
            var val = line[(idx + 1)..].Trim();
            switch (key)
            {
                case "public_key":
                    Flush();
                    current = Convert.ToBase64String(Convert.FromHexString(val));
                    tx = 0; rx = 0; hs = null;
                    break;
                case "tx_bytes": ulong.TryParse(val, out tx); break;
                case "rx_bytes": ulong.TryParse(val, out rx); break;
                case "last_handshake_time_sec":
                    if (long.TryParse(val, out var sec) && sec > 0)
                        hs = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
                    break;
            }
        }
        Flush();
        return result;
    }
}
