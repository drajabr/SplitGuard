using System.Net;
using System.Net.Sockets;
using SplitGuard.Models;

namespace SplitGuard.Services;

public record TunnelStats(Dictionary<string, (double UpBps, double DownBps, DateTime? Handshake)> PerPeer);

public class TunnelManager : IDisposable
{
    readonly Dictionary<string, ActiveTunnel> _active = new();
    readonly Timer _timer;
    readonly object _gate = new();

    public event Action<string, TunnelStats>? StatsUpdated;

    class ActiveTunnel
    {
        public required WireGuardAdapter Adapter;
        public Dictionary<string, (ulong Tx, ulong Rx, DateTime At)> Previous = new();
    }

    public TunnelManager()
    {
        _timer = new Timer(_ => Poll(), null, 1000, 1000);
    }

    public bool IsConnected(string name)
    {
        lock (_gate) return _active.ContainsKey(name);
    }

    public void Connect(TunnelConfig config)
    {
        var privateKey = Convert.FromBase64String(RuleStore.Unprotect(config.PrivateKeyProtected));
        var peers = new List<(byte[], byte[]?, IPEndPoint?, IReadOnlyList<(IPAddress, byte)>, ushort)>();
        var endpointIps = new List<IPAddress>();
        bool fullTunnel = false;

        foreach (var p in config.Peers)
        {
            var pub = Convert.FromBase64String(p.PublicKey);
            byte[]? psk = p.PresharedKeyProtected is null ? null
                : Convert.FromBase64String(RuleStore.Unprotect(p.PresharedKeyProtected));
            var endpoint = ResolveEndpoint(p.Endpoint);
            if (endpoint is not null) endpointIps.Add(endpoint.Address);
            var allowed = new List<(IPAddress, byte)>();
            foreach (var cidr in p.AllowedIps)
            {
                if (!WireGuardConf.TryParseCidr(cidr, out var net, out var prefix))
                    throw new InvalidOperationException($"Invalid allowed IP: {cidr}");
                if (prefix == 0) fullTunnel = true;
                allowed.Add((net, (byte)prefix));
            }
            peers.Add((pub, psk, endpoint, allowed, p.PersistentKeepalive));
        }

        var adapter = WireGuardAdapter.Create(config.Name);
        try
        {
            adapter.SetConfiguration(privateKey, config.ListenPort, peers);
            foreach (var addr in config.Addresses)
            {
                if (WireGuardConf.TryParseCidr(addr, out var ip, out var prefix))
                    Netio.AddAddress(adapter.Luid, ip, (byte)prefix);
            }
            foreach (var (_, _, _, allowed, _) in peers)
            {
                foreach (var (ip, cidr) in allowed)
                {
                    if (cidr == 0)
                    {
                        // Default-route split: two /1 routes win over the existing default without replacing it.
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            Netio.AddRoute(adapter.Luid, IPAddress.Parse("0.0.0.0"), 1);
                            Netio.AddRoute(adapter.Luid, IPAddress.Parse("128.0.0.0"), 1);
                        }
                        else
                        {
                            Netio.AddRoute(adapter.Luid, IPAddress.Parse("::"), 1);
                            Netio.AddRoute(adapter.Luid, IPAddress.Parse("8000::"), 1);
                        }
                    }
                    else
                    {
                        Netio.AddRoute(adapter.Luid, ip, cidr);
                    }
                }
            }
            if (fullTunnel)
                foreach (var ep in endpointIps)
                    Netio.AddEndpointHostRoute(ep);
            adapter.SetState(true);
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
        lock (_gate) _active[config.Name] = new ActiveTunnel { Adapter = adapter };
    }

    public void Disconnect(string name)
    {
        ActiveTunnel? tunnel;
        lock (_gate)
        {
            if (!_active.Remove(name, out tunnel)) return;
        }
        tunnel!.Adapter.Dispose(); // destroys adapter, its addresses, and its routes
    }

    void Poll()
    {
        List<(string Name, ActiveTunnel T)> snapshot;
        lock (_gate) snapshot = _active.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (name, tunnel) in snapshot)
        {
            List<PeerStats> stats;
            try { stats = tunnel.Adapter.GetStats(); }
            catch { continue; }
            var now = DateTime.UtcNow;
            var result = new Dictionary<string, (double, double, DateTime?)>();
            foreach (var s in stats)
            {
                var key = Convert.ToBase64String(s.PublicKey);
                double up = 0, down = 0;
                if (tunnel.Previous.TryGetValue(key, out var prev))
                {
                    var dt = (now - prev.At).TotalSeconds;
                    // Counters can reset on rekey/reconnect; clamp so ulong subtraction
                    // never wraps into a bogus exabyte/sec spike.
                    if (dt > 0)
                    {
                        if (s.TxBytes >= prev.Tx) up = (s.TxBytes - prev.Tx) / dt;
                        if (s.RxBytes >= prev.Rx) down = (s.RxBytes - prev.Rx) / dt;
                    }
                }
                tunnel.Previous[key] = (s.TxBytes, s.RxBytes, now);
                result[key] = (up, down, s.LastHandshakeUtc);
            }
            StatsUpdated?.Invoke(name, new TunnelStats(result));
        }
    }

    static IPEndPoint? ResolveEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0) throw new InvalidOperationException($"Endpoint must be host:port — got '{endpoint}'");
        var host = endpoint[..idx].Trim('[', ']');
        if (!int.TryParse(endpoint[(idx + 1)..], out var port))
            throw new InvalidOperationException($"Invalid endpoint port in '{endpoint}'");
        if (!IPAddress.TryParse(host, out var ip))
        {
            var addrs = Dns.GetHostAddresses(host);
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault()
                ?? throw new InvalidOperationException($"Cannot resolve endpoint host '{host}'");
        }
        return new IPEndPoint(ip, port);
    }

    public void DisconnectAll()
    {
        List<string> names;
        lock (_gate) names = _active.Keys.ToList();
        foreach (var n in names) Disconnect(n);
    }

    public void Dispose()
    {
        _timer.Dispose();
        DisconnectAll();
    }
}
