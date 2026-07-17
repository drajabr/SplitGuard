using SplitGuard.Services;

namespace SplitGuard.Droid;

// ISplitDnsService for Android: same rule ids/namespaces as NRPT on Windows, but the
// "backend" is an in-memory table consumed by the in-tunnel DnsForwarder. Rules apply
// instantly (the forwarder reads a snapshot per query); nothing persists — the view
// model re-applies rules on every connect, exactly like it does against NRPT.
public class AndroidSplitDnsService : ISplitDnsService
{
    public static AndroidSplitDnsService Instance { get; } = new();

    readonly object _gate = new();
    readonly Dictionary<string, NrptRule> _rules = new();     // id → rule
    string[] _catchAll = Array.Empty<string>();

    public bool IsPolicyManaged => false;

    // Snapshot for the forwarder: (namespace, server) pairs + ordered catch-all chain.
    public (List<(string Ns, string Server)> Rules, string[] CatchAll) Snapshot()
    {
        lock (_gate)
        {
            var list = new List<(string, string)>();
            foreach (var r in _rules.Values)
                foreach (var ns in r.Namespaces)
                    if (r.Servers.Length > 0)
                        list.Add((ns, r.Servers[0]));
            return (list, _catchAll);
        }
    }

    public void ApplyDomain(string tunnelName, string peerPublicKey, string domain, string dnsServer)
    {
        lock (_gate)
        {
            var id = SplitDnsRules.RuleId(tunnelName, peerPublicKey, domain);
            _rules[id] = new NrptRule(id, new[] { SplitDnsRules.DomainToNamespace(domain) }, new[] { dnsServer });
        }
    }

    public void ApplyPeerRules(string tunnelName, string peerPublicKey, IEnumerable<string> domains, string dnsServer)
    {
        foreach (var d in domains) ApplyDomain(tunnelName, peerPublicKey, d, dnsServer);
    }

    public void RemovePeerRules(string tunnelName, string peerPublicKey)
    {
        var prefix = $"WGSDNS|{tunnelName}|{SplitDnsRules.Short(peerPublicKey)}|";
        lock (_gate)
            foreach (var id in _rules.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _rules.Remove(id);
    }

    public void RemoveByTunnel(string tunnelName)
    {
        var prefix = $"WGSDNS|{tunnelName}|";
        lock (_gate)
            foreach (var id in _rules.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _rules.Remove(id);
    }

    public void SetCatchAll(string[] orderedServers) { lock (_gate) _catchAll = orderedServers; }
    public void RemoveCatchAll() { lock (_gate) _catchAll = Array.Empty<string>(); }

    public List<NrptRule> GetTaggedRules() { lock (_gate) return _rules.Values.ToList(); }

    public void RemoveTagged(IEnumerable<string> ids)
    {
        lock (_gate) foreach (var id in ids) _rules.Remove(id);
    }

    public void RemoveAllTagged() { lock (_gate) _rules.Clear(); }
}
