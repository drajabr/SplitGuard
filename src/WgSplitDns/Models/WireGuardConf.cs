using System.Net;
using System.Text;

namespace WgSplitDns.Models;

// Plaintext key material lives only in this transient parse result; callers protect it immediately.
public class ParsedTunnel
{
    public string? Name { get; set; }
    public string PrivateKey { get; set; } = "";
    public List<string> Addresses { get; } = new();
    public string? InterfaceDns { get; set; }
    public List<ParsedPeer> Peers { get; } = new();
    public List<string> Warnings { get; } = new();
}

public class ParsedPeer
{
    public string PublicKey { get; set; } = "";
    public string? PresharedKey { get; set; }
    public string Endpoint { get; set; } = "";
    public List<string> AllowedIps { get; } = new();
    public ushort PersistentKeepalive { get; set; }
}

public static class WireGuardConf
{
    public static ParsedTunnel Parse(string text)
    {
        var result = new ParsedTunnel();
        ParsedPeer? peer = null;
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').ToLowerInvariant();
                if (section == "peer")
                {
                    peer = new ParsedPeer();
                    result.Peers.Add(peer);
                }
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            if (section == "interface")
            {
                switch (key)
                {
                    case "privatekey": result.PrivateKey = value; break;
                    case "address": result.Addresses.AddRange(SplitList(value)); break;
                    case "dns": result.InterfaceDns = SplitList(value).FirstOrDefault(); break;
                    default: result.Warnings.Add($"Ignored [Interface] {key}"); break;
                }
            }
            else if (section == "peer" && peer is not null)
            {
                switch (key)
                {
                    case "publickey": peer.PublicKey = value; break;
                    case "presharedkey": peer.PresharedKey = value; break;
                    case "endpoint": peer.Endpoint = value; break;
                    case "allowedips": peer.AllowedIps.AddRange(SplitList(value)); break;
                    case "persistentkeepalive": if (ushort.TryParse(value, out var ka)) peer.PersistentKeepalive = ka; break;
                    default: result.Warnings.Add($"Ignored [Peer] {key}"); break;
                }
            }
        }
        return result;
    }

    // Attach an imported DNS= line to the peer whose AllowedIPs contain it; first peer as fallback. Never global.
    public static ParsedPeer? PeerForDns(ParsedTunnel tunnel)
    {
        if (tunnel.InterfaceDns is null || tunnel.Peers.Count == 0) return null;
        if (!IPAddress.TryParse(tunnel.InterfaceDns, out var ip)) return tunnel.Peers[0];
        return tunnel.Peers.FirstOrDefault(p => p.AllowedIps.Any(c => CidrContains(c, ip))) ?? tunnel.Peers[0];
    }

    public static bool CidrContains(string cidr, IPAddress ip)
    {
        if (!TryParseCidr(cidr, out var network, out var prefix)) return false;
        var nb = network.GetAddressBytes();
        var ib = ip.GetAddressBytes();
        if (nb.Length != ib.Length) return false;
        int bits = prefix;
        for (int i = 0; i < nb.Length && bits > 0; i++, bits -= 8)
        {
            byte mask = bits >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - bits));
            if ((nb[i] & mask) != (ib[i] & mask)) return false;
        }
        return true;
    }

    public static bool TryParseCidr(string cidr, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var parts = cidr.Split('/');
        if (!IPAddress.TryParse(parts[0].Trim(), out var ip)) return false;
        int max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1].Trim(), out prefix) || prefix < 0 || prefix > max) return false;
        }
        else
        {
            prefix = max;
        }
        network = ip;
        return true;
    }

    public static string Serialize(string privateKey, TunnelConfig t, Func<PeerConfig, string?> pskLookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {privateKey}");
        sb.AppendLine($"Address = {string.Join(", ", t.Addresses)}");
        foreach (var p in t.Peers)
        {
            sb.AppendLine();
            sb.AppendLine("[Peer]");
            sb.AppendLine($"PublicKey = {p.PublicKey}");
            var psk = pskLookup(p);
            if (!string.IsNullOrEmpty(psk)) sb.AppendLine($"PresharedKey = {psk}");
            sb.AppendLine($"Endpoint = {p.Endpoint}");
            sb.AppendLine($"AllowedIPs = {string.Join(", ", p.AllowedIps)}");
            if (p.PersistentKeepalive > 0) sb.AppendLine($"PersistentKeepalive = {p.PersistentKeepalive}");
        }
        return sb.ToString();
    }

    static IEnumerable<string> SplitList(string value) =>
        value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
}
