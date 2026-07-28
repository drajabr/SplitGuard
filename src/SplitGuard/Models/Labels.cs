namespace SplitGuard.Models;

// One canonical way to name a peer in user-visible text: "peer@tunnel".
//
// Every message that mentions a peer needs its tunnel too, because a peer name is only unique
// within its tunnel — and the interesting cases (failover between overlapping allowed-IPs,
// duplicate metrics, a peer with no health signal) routinely span two tunnels. Naming just the
// tunnel ("failover office → homelab") hides which peer moved when both live on one tunnel;
// naming just the peer ("peer switch") hides where it lives. "main@office" says both, always,
// so the reader never has to guess which half was omitted.
public static class Labels
{
    // Last 4 characters of the base64 public key — the SAME tail TunnelViewModel.PublicKeyShort
    // prints in the identity row ("abcdEFGH…xY0="), so a notification and the on-screen key chip
    // can be matched by eye. A WireGuard key is 44 chars ending in '=', so the tail carries that
    // pad; keeping it is deliberate — trimming would shift the window out of step with the chip.
    // NOT SplitDnsRules.Short: that is the FIRST 8 and is frozen into every NRPT rule id.
    public static string KeyTail(string? publicKey)
    {
        var k = (publicKey ?? "").Trim();
        return k.Length > 4 ? k[^4..] : k;
    }

    // "peer", "peer 1", "PEER  12", "peer3" — the placeholder the UI itself invents for an unnamed
    // peer (see TunnelCard.BuildDetail). It identifies nothing, so treat it as no name at all.
    // "peerless", "peer-b" and "peer east" are real names and survive. Expects a trimmed name.
    static bool IsGenericPeerName(string name)
    {
        if (!name.StartsWith("peer", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var c in name.AsSpan(4).Trim()) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    // The peer half on its own, for UI that is already inside a tunnel's card or notification.
    // An absent or placeholder name degrades to the key tail, and only then to a bare "peer" —
    // the label must never degrade to a lonely "@tunnel".
    public static string PeerName(string? peerName, string? peerPublicKey = null)
    {
        var peer = (peerName ?? "").Trim();
        if (peer.Length == 0 || IsGenericPeerName(peer))
        {
            var tail = KeyTail(peerPublicKey);
            peer = tail.Length > 0 ? tail : "peer";
        }
        return peer;
    }

    public static string PeerAt(string? peerName, string? tunnelName, string? peerPublicKey = null)
    {
        var peer = PeerName(peerName, peerPublicKey);
        var tunnel = (tunnelName ?? "").Trim();
        return tunnel.Length == 0 ? peer : $"{peer}@{tunnel}";
    }
}
