using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SplitGuard.Models;

namespace SplitGuard.Services;

// Per-peer live signals published every poll tick. FailoverRole: null = not part of an
// overlap group, otherwise "active" / "standby".
public record PeerLive(double UpBps, double DownBps, DateTime? Handshake,
    double? PingMs, bool? PingOk, bool Healthy, string? FailoverRole);

public record TunnelStats(Dictionary<string, PeerLive> PerPeer);

public class TunnelManager : IDisposable
{
    // Health model, one rule per signal:
    //  - Ping host set (and the probe actually tests this path): PingDownCount
    //    consecutive failures = down, PingUpCount consecutive successes = up. The
    //    counts ARE the hysteresis — raise them to tolerate a flappier link.
    //  - No ping host: handshake freshness decides. A handshake refreshes at most every
    //    ~2 min under traffic/keepalive, so >3 min old = down; a fresh one = up.
    // New connections get a grace period to complete their first handshake.
    static readonly TimeSpan HandshakeStale = TimeSpan.FromSeconds(180);
    static readonly TimeSpan HandshakeGrace = TimeSpan.FromSeconds(60);
    const int DefaultPingTimeoutSec = 3;
    const int DefaultPingCount = 3;
    // Probe cadence is decoupled from keepalive and per-peer configurable; this is the
    // fallback when the peer leaves it blank.
    const int DefaultPingPeriodSec = 5;
    const uint StandbyMetricBase = 400;  // far above any interface-metric difference

    readonly Dictionary<string, ActiveTunnel> _active = new();
    readonly Timer _timer;
    readonly object _gate = new();
    int _polling;

    public event Action<string, TunnelStats>? StatsUpdated;
    // "10.0.0.0/24: failover tunnelA → tunnelB" style message for the UI.
    public event Action<string>? FailoverChanged;

    class PeerRuntime
    {
        public required string Key; // base64 public key
        public required byte[] PublicKey;
        public byte[]? Psk;
        public IPEndPoint? Endpoint;
        public ushort Keepalive;
        public required List<(IPAddress Ip, byte Cidr)> AllowedIps;
        public int Metric;
        public int PingTimeoutMs;
        public int PingPeriodSec;
        public int PingDownCount;
        public int PingUpCount;
        public string? PingHost;
        public DateTime ConnectedAt;

        public (ulong Tx, ulong Rx, DateTime At)? Previous;
        public DateTime? LastHandshake;
        public double UpBps, DownBps;

        public double? LastPingMs;
        public bool? LastPingOk;
        public int PingFailStreak;
        public int PingOkStreak;
        public DateTime NextPingDue;
        public bool PingInFlight;

        public bool Healthy = true;
        public string? FailoverRole;
        // True when a /32 probe route pins this peer's ping host to its own adapter, so
        // pings test this path even while the peer is standby.
        public bool HasProbeRoute;
    }

    class ActiveTunnel
    {
        public required WireGuardAdapter Adapter;
        public required string Name;
        public required byte[] PrivateKey;
        public required ushort ListenPort;
        public required List<PeerRuntime> Peers;
        // Overlapped CIDR (within this tunnel) → peer key that currently owns it in the
        // WireGuard config; peers on one adapter can't share an allowed IP, so ownership
        // moves on failover via SetConfiguration.
        public Dictionary<string, string> IntraOwner = new();
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
        var runtimes = new List<PeerRuntime>();
        var endpointIps = new List<IPAddress>();
        bool fullTunnel = false;
        var now = DateTime.UtcNow;

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
            runtimes.Add(new PeerRuntime
            {
                Key = Convert.ToBase64String(pub),
                PublicKey = pub,
                Psk = psk,
                Endpoint = endpoint,
                Keepalive = p.PersistentKeepalive,
                AllowedIps = allowed,
                Metric = p.Metric,
                PingTimeoutMs = (p.PingTimeout is >= 1 and <= 60 ? p.PingTimeout : DefaultPingTimeoutSec) * 1000,
                PingPeriodSec = p.PingPeriod is >= 1 and <= 3600 ? p.PingPeriod : DefaultPingPeriodSec,
                PingDownCount = p.PingDownCount is >= 1 and <= 100 ? p.PingDownCount : DefaultPingCount,
                PingUpCount = p.PingUpCount is >= 1 and <= 100 ? p.PingUpCount : DefaultPingCount,
                PingHost = string.IsNullOrWhiteSpace(p.PingHost) ? null : p.PingHost.Trim(),
                ConnectedAt = now,
                NextPingDue = now,
            });
        }

        var adapter = WireGuardAdapter.Create(config.Name);
        var tunnel = new ActiveTunnel
        {
            Adapter = adapter,
            Name = config.Name,
            PrivateKey = privateKey,
            ListenPort = config.ListenPort,
            Peers = runtimes,
        };
        try
        {
            // Within one tunnel WireGuard forbids the same allowed IP on two peers: give
            // each duplicated CIDR to the best-ranked peer; failover reassigns it live.
            RecomputeIntraOwners(tunnel);
            adapter.SetConfiguration(privateKey, config.ListenPort, BuildWgPeers(tunnel));
            foreach (var addr in config.Addresses)
            {
                if (WireGuardConf.TryParseCidr(addr, out var ip, out var prefix))
                    Netio.AddAddress(adapter.Luid, ip, (byte)prefix);
            }
            var added = new HashSet<string>();
            foreach (var rt in runtimes)
            {
                foreach (var (ip, cidr) in rt.AllowedIps)
                {
                    if (!added.Add(CidrKey(ip, cidr))) continue;
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
            // Probe routes: pin each unique in-tunnel ping host to its own adapter so the
            // ping tests *this* path even while the peer is standby in a failover group
            // (otherwise the probe follows the active route and says nothing about us).
            // A ping host shared by several peers can't be pinned to one adapter — skipped.
            List<string?> allPingHosts;
            lock (_gate) allPingHosts = _active.Values.SelectMany(t => t.Peers).Select(p => p.PingHost).ToList();
            foreach (var rt in runtimes)
            {
                if (rt.PingHost is null || !IPAddress.TryParse(rt.PingHost, out var probeIp)) continue;
                bool unique = runtimes.Count(p => p.PingHost == rt.PingHost) == 1
                    && !allPingHosts.Contains(rt.PingHost);
                bool inTunnel = rt.AllowedIps.Any(a => WireGuardConf.CidrContains($"{a.Ip}/{a.Cidr}", probeIp));
                if (!unique || !inTunnel) continue;
                Netio.AddRoute(adapter.Luid, probeIp,
                    (byte)(probeIp.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
                rt.HasProbeRoute = true;
            }
            adapter.SetState(true);
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
        lock (_gate) _active[config.Name] = tunnel;
        // Arbitrate immediately so a standby joining an existing overlap group never
        // competes with the active route at equal metrics.
        try { ReconcileFailover(); } catch { }
    }

    public void Disconnect(string name)
    {
        ActiveTunnel? tunnel;
        lock (_gate)
        {
            if (!_active.Remove(name, out tunnel)) return;
        }
        tunnel!.Adapter.Dispose(); // destroys adapter, its addresses, and its routes
        try { ReconcileFailover(); } catch { }
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // skip overlapping ticks
        try
        {
            List<ActiveTunnel> snapshot;
            lock (_gate) snapshot = _active.Values.ToList();
            var now = DateTime.UtcNow;

            foreach (var tunnel in snapshot)
            {
                List<PeerStats> stats;
                try { stats = tunnel.Adapter.GetStats(); }
                catch { continue; }
                foreach (var s in stats)
                {
                    var key = Convert.ToBase64String(s.PublicKey);
                    var rt = tunnel.Peers.FirstOrDefault(p => p.Key == key);
                    if (rt is null) continue;
                    double up = 0, down = 0;
                    if (rt.Previous is { } prev)
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
                    rt.Previous = (s.TxBytes, s.RxBytes, now);
                    rt.UpBps = up;
                    rt.DownBps = down;
                    rt.LastHandshake = s.LastHandshakeUtc;
                }
                SchedulePings(tunnel, now);
            }

            // Never let an arbitration hiccup escape the timer callback — that would
            // terminate the process.
            try { ReconcileFailover(); } catch { }

            foreach (var tunnel in snapshot)
            {
                var result = tunnel.Peers.ToDictionary(
                    p => p.Key,
                    p => new PeerLive(p.UpBps, p.DownBps, p.LastHandshake,
                        p.LastPingMs, p.LastPingOk, p.Healthy, p.FailoverRole));
                StatsUpdated?.Invoke(tunnel.Name, new TunnelStats(result));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    // ---- keepalive pings ---------------------------------------------------------

    void SchedulePings(ActiveTunnel tunnel, DateTime now)
    {
        foreach (var rt in tunnel.Peers)
        {
            if (rt.PingHost is null || rt.PingInFlight || now < rt.NextPingDue) continue;
            if (!IPAddress.TryParse(rt.PingHost, out var target)) continue;
            // The in-flight guard prevents overlap, so a timeout longer than the period
            // simply stretches the effective interval.
            rt.NextPingDue = now.AddSeconds(rt.PingPeriodSec);
            rt.PingInFlight = true;
            var timeout = rt.PingTimeoutMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, timeout);
                    if (reply.Status == IPStatus.Success)
                    {
                        rt.LastPingMs = reply.RoundtripTime;
                        rt.LastPingOk = true;
                        rt.PingFailStreak = 0;
                        rt.PingOkStreak++;
                    }
                    else
                    {
                        rt.LastPingMs = null;
                        rt.LastPingOk = false;
                        rt.PingFailStreak++;
                        rt.PingOkStreak = 0;
                    }
                }
                catch
                {
                    rt.LastPingMs = null;
                    rt.LastPingOk = false;
                    rt.PingFailStreak++;
                    rt.PingOkStreak = 0;
                }
                finally
                {
                    rt.PingInFlight = false;
                }
            });
        }
    }

    // ---- health + failover arbitration --------------------------------------------

    static string CidrKey(IPAddress ip, byte cidr)
    {
        // Mask host bits so equivalent entries group regardless of how they were typed.
        var bytes = ip.GetAddressBytes();
        int bits = cidr;
        for (int i = 0; i < bytes.Length; i++, bits -= 8)
        {
            if (bits >= 8) continue;
            bytes[i] = bits <= 0 ? (byte)0 : (byte)(bytes[i] & (0xFF << (8 - bits)));
        }
        return $"{new IPAddress(bytes)}/{cidr}";
    }

    void EvaluateHealth(List<(ActiveTunnel Tunnel, PeerRuntime Peer)> members,
        Dictionary<string, List<(ActiveTunnel Tunnel, PeerRuntime Peer)>> groups, DateTime now)
    {
        foreach (var (tunnel, rt) in members)
        {
            // Ping decides health when a ping host is set AND the probe actually tests
            // this path. A standby's ping usually routes through the *active* tunnel (the
            // probe target sits inside the shared range) — unless a /32 probe route pins
            // it here — so an untestable probe falls back to the handshake rule.
            bool pingBased = rt.PingHost is not null
                && IPAddress.TryParse(rt.PingHost, out var pingIp)
                && !PingRoutesElsewhere(tunnel, rt, pingIp, groups);

            if (pingBased)
            {
                // Count-based hysteresis with separate directions: the state only flips
                // once a full streak agrees, and holds between thresholds. Down and up
                // counts are independent so recovery can be judged more cautiously.
                if (rt.PingFailStreak >= rt.PingDownCount) rt.Healthy = false;
                else if (rt.PingOkStreak >= rt.PingUpCount) rt.Healthy = true;
            }
            else
            {
                rt.Healthy = rt.LastHandshake is { } h
                    ? now - h < HandshakeStale
                    : now - rt.ConnectedAt < HandshakeGrace;
            }
        }
    }

    bool PingRoutesElsewhere(ActiveTunnel tunnel, PeerRuntime rt, IPAddress pingIp,
        Dictionary<string, List<(ActiveTunnel Tunnel, PeerRuntime Peer)>> groups)
    {
        if (rt.HasProbeRoute) return false; // pinned to this adapter: always meaningful
        foreach (var (key, members) in groups)
        {
            if (!WireGuardConf.CidrContains(key, pingIp)) continue;
            var active = _groupActive.TryGetValue(key, out var a) ? a : null;
            if (active is not null && active != MemberKey(tunnel, rt)) return true;
        }
        return false;
    }

    static string MemberKey(ActiveTunnel t, PeerRuntime p) => $"{t.Name}\n{p.Key}";

    // Applied state, so reconcile only touches the system when something changed.
    // Everything below _reconcileGate (group state, applied metrics, peer health fields)
    // is only read/written inside ReconcileFailoverCore.
    readonly object _reconcileGate = new();
    readonly Dictionary<string, string> _groupActive = new();          // group cidr → member key
    readonly Dictionary<(ulong Luid, string Cidr), uint> _appliedMetric = new();

    // Serialized: called concurrently from the poll timer and from Connect/Disconnect
    // background tasks, over shared dictionaries and peer health state.
    void ReconcileFailover()
    {
        lock (_reconcileGate) ReconcileFailoverCore();
    }

    void ReconcileFailoverCore()
    {
        List<ActiveTunnel> snapshot;
        lock (_gate) snapshot = _active.Values.ToList();
        var now = DateTime.UtcNow;

        // Group every connected peer's allowed CIDRs; a group is a CIDR claimed twice.
        var groups = new Dictionary<string, List<(ActiveTunnel Tunnel, PeerRuntime Peer)>>();
        var all = new List<(ActiveTunnel Tunnel, PeerRuntime Peer)>();
        foreach (var t in snapshot)
            foreach (var p in t.Peers)
            {
                all.Add((t, p));
                foreach (var (ip, cidr) in p.AllowedIps)
                {
                    var key = CidrKey(ip, cidr);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                    if (!list.Any(m => ReferenceEquals(m.Peer, p))) list.Add((t, p));
                }
            }
        foreach (var key in groups.Where(g => g.Value.Count < 2).Select(g => g.Key).ToList())
            groups.Remove(key);

        EvaluateHealth(all, groups, now);

        // Reset roles; grouped members get theirs below.
        foreach (var (_, p) in all) p.FailoverRole = null;

        var wgDirty = new HashSet<ActiveTunnel>();
        foreach (var (key, members) in groups)
        {
            var ordered = members
                .OrderBy(m => m.Peer.Metric)
                .ThenBy(m => m.Tunnel.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Tunnel.Peers.IndexOf(m.Peer))
                .ToList();
            var active = ordered.FirstOrDefault(m => m.Peer.Healthy);
            if (active.Peer is null) active = ordered[0]; // nothing healthy: keep the best-ranked
            var activeKey = MemberKey(active.Tunnel, active.Peer);

            foreach (var m in ordered)
                m.Peer.FailoverRole = MemberKey(m.Tunnel, m.Peer) == activeKey ? "active" : "standby";

            if (!_groupActive.TryGetValue(key, out var prevActive) || prevActive != activeKey)
            {
                var from = prevActive?.Split('\n')[0];
                _groupActive[key] = activeKey;
                if (prevActive is not null && groups.Count > 0)
                    FailoverChanged?.Invoke($"{key}: {(from == active.Tunnel.Name ? "peer switch" : $"failover {from} → {active.Tunnel.Name}")}");
            }

            // Route metrics: the active member's adapter wins; every other adapter in the
            // group is pushed far back. Adapters keep one route per CIDR regardless of how
            // many of their peers claim it.
            uint standby = StandbyMetricBase;
            foreach (var adapterMembers in ordered.GroupBy(m => m.Tunnel))
            {
                var t = adapterMembers.Key;
                var metric = ReferenceEquals(t, active.Tunnel) ? 0u : standby += 16;
                ApplyMetric(t, key, metric);

                // Within one adapter the CIDR must live on exactly one peer: the group's
                // active peer if it's here, else the adapter's best healthy member.
                var owner = ReferenceEquals(t, active.Tunnel)
                    ? active.Peer
                    : (adapterMembers.FirstOrDefault(m => m.Peer.Healthy).Peer ?? adapterMembers.First().Peer);
                if (adapterMembers.Count() > 1 &&
                    (!t.IntraOwner.TryGetValue(key, out var cur) || cur != owner.Key))
                {
                    t.IntraOwner[key] = owner.Key;
                    wgDirty.Add(t);
                }
            }
        }

        // Groups that dissolved (disconnect / config change): drop state and restore the
        // surviving route — if any — to the default metric so it isn't stuck on standby.
        foreach (var stale in _groupActive.Keys.Where(k => !groups.ContainsKey(k)).ToList())
        {
            _groupActive.Remove(stale);
            foreach (var t in snapshot)
            {
                _appliedMetric.Remove((t.Adapter.Luid, stale));
                if (t.Peers.Any(p => p.AllowedIps.Any(a => CidrKey(a.Ip, a.Cidr) == stale)))
                    ApplyMetric(t, stale, 0);
            }
        }
        // Purge applied-metric state for adapters that no longer exist.
        var liveLuids = snapshot.Select(t => t.Adapter.Luid).ToHashSet();
        foreach (var k in _appliedMetric.Keys.Where(k => !liveLuids.Contains(k.Luid)).ToList())
            _appliedMetric.Remove(k);

        foreach (var t in wgDirty)
        {
            try { t.Adapter.SetConfiguration(t.PrivateKey, t.ListenPort, BuildWgPeers(t)); }
            catch { }
        }
    }

    void ApplyMetric(ActiveTunnel tunnel, string groupCidr, uint metric)
    {
        var stateKey = (tunnel.Adapter.Luid, groupCidr);
        if (_appliedMetric.TryGetValue(stateKey, out var cur) && cur == metric) return;
        _appliedMetric[stateKey] = metric;
        if (!WireGuardConf.TryParseCidr(groupCidr, out var ip, out var prefix)) return;
        if (prefix == 0)
        {
            // A /0 group is installed as the two /1 halves.
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                Netio.SetRouteMetric(tunnel.Adapter.Luid, IPAddress.Parse("0.0.0.0"), 1, metric);
                Netio.SetRouteMetric(tunnel.Adapter.Luid, IPAddress.Parse("128.0.0.0"), 1, metric);
            }
            else
            {
                Netio.SetRouteMetric(tunnel.Adapter.Luid, IPAddress.Parse("::"), 1, metric);
                Netio.SetRouteMetric(tunnel.Adapter.Luid, IPAddress.Parse("8000::"), 1, metric);
            }
        }
        else
        {
            Netio.SetRouteMetric(tunnel.Adapter.Luid, ip, (byte)prefix, metric);
        }
    }

    // Initial within-tunnel ownership of duplicated CIDRs: best rank wins.
    static void RecomputeIntraOwners(ActiveTunnel tunnel)
    {
        tunnel.IntraOwner.Clear();
        var claims = new Dictionary<string, List<PeerRuntime>>();
        foreach (var p in tunnel.Peers)
            foreach (var (ip, cidr) in p.AllowedIps)
            {
                var key = CidrKey(ip, cidr);
                if (!claims.TryGetValue(key, out var list)) claims[key] = list = new();
                if (!list.Contains(p)) list.Add(p);
            }
        foreach (var (key, list) in claims.Where(c => c.Value.Count > 1))
            tunnel.IntraOwner[key] = list.OrderBy(p => p.Metric).ThenBy(p => tunnel.Peers.IndexOf(p)).First().Key;
    }

    static List<(byte[], byte[]?, IPEndPoint?, IReadOnlyList<(IPAddress, byte)>, ushort)> BuildWgPeers(ActiveTunnel tunnel) =>
        tunnel.Peers.Select(p => (p.PublicKey, p.Psk, p.Endpoint,
            (IReadOnlyList<(IPAddress, byte)>)p.AllowedIps
                .Where(a => !tunnel.IntraOwner.TryGetValue(CidrKey(a.Ip, a.Cidr), out var owner) || owner == p.Key)
                .ToList(),
            p.Keepalive)).ToList();

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
