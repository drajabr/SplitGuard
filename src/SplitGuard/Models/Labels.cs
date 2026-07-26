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
    // Unnamed peers are normal (the name box is optional), so fall back to the same short
    // public-key form the NRPT rule ids and the key chips use, and only then to a bare "peer" —
    // the label must never degrade to a lonely "@tunnel".
    public static string PeerAt(string? peerName, string? tunnelName, string? peerPublicKey = null)
    {
        var peer = (peerName ?? "").Trim();
        if (peer.Length == 0 && !string.IsNullOrWhiteSpace(peerPublicKey))
            peer = Services.SplitDnsRules.Short(peerPublicKey!.Trim());
        if (peer.Length == 0) peer = "peer";
        var tunnel = (tunnelName ?? "").Trim();
        return tunnel.Length == 0 ? peer : $"{peer}@{tunnel}";
    }
}
