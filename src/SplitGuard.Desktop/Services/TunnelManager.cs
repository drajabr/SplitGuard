using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SplitGuard.Models;

namespace SplitGuard.Services;

public class TunnelManager : ITunnelEngine
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
    // Never raised here: on Windows every disconnect is user-initiated through the VM.
    public event Action<string>? Disconnected { add { } remove { } }

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
        // When this peer last BECAME its group's active member: a freshly promoted peer
        // gets a handshake grace window (its pre-promotion handshake age says nothing).
        public DateTime PromotedAt;
        // Original "host:port" when the host is a NAME (null for literal IPs): a stale
        // peer gets its hostname re-resolved periodically — DDNS servers move, and the
        // one-shot resolve at Connect froze the old IP forever.
        public string? EndpointSpec;
        public DateTime NextResolveDue;
        public bool ResolveInFlight;

        public (ulong Tx, ulong Rx, DateTime At)? Previous;
        public DateTime? LastHandshake;
        public double UpBps, DownBps;
        public ulong TotalTx, TotalRx;

        public double? LastPingMs;
        public bool? LastPingOk;
        public int PingFailStreak;
        public int PingOkStreak;
        public DateTime NextPingDue;
        public bool PingInFlight;
        // Rolling window of recent probe outcomes for the avg-RTT / loss readout.
        public readonly Queue<(bool Ok, double Ms)> PingWindow = new();
        public double? AvgPingMs;
        public double? PingLoss;

        public bool Healthy = true;
        public string? FailoverRole;
        // True when a /32 probe route pins this peer's ping host to its own adapter, so
        // pings test this path even while the peer is standby.
        public bool HasProbeRoute;
        // Whether probe outcomes currently test THIS peer's path (set each reconcile from
        // the same predicate EvaluateHealth uses). While false, completed pings must not
        // touch the health streaks — a standby probing through the ACTIVE tunnel was
        // banking fail/ok streaks about the wrong path, and consuming them on promotion
        // produced a ~1Hz failover flap through the dead tunnel.
        public bool PingMeaningful = true;
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
        // Endpoint host routes pinned on the *physical* adapter (full-tunnel only); the
        // tunnel adapter's teardown doesn't remove them, so Disconnect must.
        public List<Netio.HostRoute> EndpointRoutes = new();
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
                PromotedAt = now,
                EndpointSpec = EndpointHostIsName(p.Endpoint) ? p.Endpoint.Trim() : null,
                NextResolveDue = now.AddSeconds(90),
                NextPingDue = now,
            });
        }

        // Sweep stale NetworkList profiles from the pre-stable-GUID era ("office 2", …):
        // NLA would otherwise resurrect the highest suffix for the fresh adapter's profile.
        NetworkProfiles.SweepStale(config.Name);
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
                {
                    if (Netio.AddEndpointHostRoute(ep) is { } route)
                        tunnel.EndpointRoutes.Add(route);
                }
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
            foreach (var route in tunnel.EndpointRoutes) route.Delete();
            adapter.Dispose();
            throw;
        }
        lock (_gate)
        {
            // Never leak an adapter by overwriting a same-name entry: lifecycle ops are
            // serialized by the caller, but an orphan here would keep routing forever.
            if (_active.Remove(config.Name, out var previous))
            {
                try { previous.Adapter.Dispose(); } catch { }
                foreach (var r in previous.EndpointRoutes) { try { r.Delete(); } catch { } }
            }
            _active[config.Name] = tunnel;
        }
        // Arbitrate immediately so a standby joining an existing overlap group never
        // competes with the active route at equal metrics.
        try { ReconcileFailover(); } catch { }
    }

    // Whether the endpoint's host part is a DNS name (vs a literal IP).
    static bool EndpointHostIsName(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0) return false;
        return !IPAddress.TryParse(endpoint[..idx].Trim('[', ']'), out _);
    }

    public void Disconnect(string name)
    {
        ActiveTunnel? tunnel;
        lock (_gate)
        {
            if (!_active.Remove(name, out tunnel)) return;
        }
        tunnel!.Adapter.Dispose(); // destroys adapter, its addresses, and its routes
        foreach (var route in tunnel.EndpointRoutes) route.Delete(); // pinned on the physical adapter
        try { ReconcileFailover(); } catch { }
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // skip overlapping ticks
        // Nothing may escape a timer callback — an unhandled exception there kills the
        // process. Anything recoverable simply retries next second.
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
                    rt.TotalTx = s.TxBytes;
                    rt.TotalRx = s.RxBytes;
                    rt.LastHandshake = s.LastHandshakeUtc;
                }
                SchedulePings(tunnel, now);
                ScheduleEndpointReresolve(tunnel, now);
            }

            ReconcileFailover();

            foreach (var tunnel in snapshot)
            {
                var result = new Dictionary<string, PeerLive>(tunnel.Peers.Count);
                foreach (var p in tunnel.Peers) // indexer, not ToDictionary: duplicate keys must not throw
                    result[p.Key] = new PeerLive(p.UpBps, p.DownBps, p.LastHandshake,
                        p.LastPingMs, p.LastPingOk, p.Healthy, p.FailoverRole,
                        p.AvgPingMs, p.PingLoss, p.TotalTx, p.TotalRx);
                StatsUpdated?.Invoke(tunnel.Name, new TunnelStats(result));
            }
        }
        catch { }
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
                bool ok = false;
                double ms = 0;
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, timeout);
                    ok = reply.Status == IPStatus.Success;
                    ms = reply.RoundtripTime;
                }
                catch { ok = false; }

                // The live readout always updates; the HEALTH streaks and rolling window
                // only count probes that actually traversed this peer's path (see
                // PingMeaningful) — foreign-path outcomes poisoned failover decisions.
                rt.LastPingMs = ok ? ms : null;
                rt.LastPingOk = ok;
                if (rt.PingMeaningful)
                {
                    if (ok) { rt.PingFailStreak = 0; rt.PingOkStreak++; }
                    else { rt.PingFailStreak++; rt.PingOkStreak = 0; }
                    // Rolling stats over the last ~20 probes (only this task touches the queue).
                    rt.PingWindow.Enqueue((ok, ms));
                    while (rt.PingWindow.Count > 20) rt.PingWindow.Dequeue();
                    var oks = rt.PingWindow.Where(w => w.Ok).Select(w => w.Ms).ToList();
                    rt.AvgPingMs = oks.Count > 0 ? oks.Average() : null;
                    rt.PingLoss = (double)rt.PingWindow.Count(w => !w.Ok) / rt.PingWindow.Count;
                }
                rt.PingInFlight = false;
            });
        }
    }

    // A peer whose handshake has gone stale and whose endpoint came from a hostname gets
    // the name re-resolved (once a minute, one in flight): if the IP moved (DDNS), the
    // driver's endpoint is updated IN PLACE via a single-peer update — never a full peer
    // replace, which would reset every peer's roamed endpoint and handshake state.
    void ScheduleEndpointReresolve(ActiveTunnel tunnel, DateTime now)
    {
        foreach (var rt in tunnel.Peers)
        {
            if (rt.EndpointSpec is null || rt.ResolveInFlight || now < rt.NextResolveDue) continue;
            var stale = rt.LastHandshake is { } h
                ? now - h >= HandshakeStale
                : now - rt.ConnectedAt >= HandshakeGrace;
            if (!stale) continue;
            rt.NextResolveDue = now.AddSeconds(60);
            rt.ResolveInFlight = true;
            var t = tunnel;
            _ = Task.Run(() =>
            {
                try
                {
                    var ep = ResolveEndpoint(rt.EndpointSpec);
                    if (ep is not null && !ep.Equals(rt.Endpoint))
                    {
                        rt.Endpoint = ep;
                        t.Adapter.UpdatePeerEndpoint(rt.PublicKey, ep);
                    }
                }
                catch { } // unresolvable right now: retried next due time
                finally { rt.ResolveInFlight = false; }
            });
        }
    }

    // ---- health + failover arbitration --------------------------------------------

    // True when this route strictly covers the given group key (broader prefix that
    // contains the key's network).
    static bool CoversKey((IPAddress Ip, byte Cidr) route, string innerKey)
    {
        if (!WireGuardConf.TryParseCidr(innerKey, out var innerIp, out var innerPrefix)) return false;
        return route.Cidr < innerPrefix
            && WireGuardConf.CidrContains($"{route.Ip}/{route.Cidr}", innerIp);
    }

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
                && !PingRoutesElsewhere(tunnel, rt, pingIp, groups)
                // Another adapter's /32 probe pin to the SAME host wins by longest prefix
                // and captures our probes — outcomes would describe that adapter's path.
                && !members.Any(m => !ReferenceEquals(m.Peer, rt)
                    && m.Peer.HasProbeRoute && m.Peer.PingHost == rt.PingHost);
            rt.PingMeaningful = pingBased;

            if (pingBased)
            {
                // Count-based hysteresis with separate directions: the state only flips
                // once a full streak agrees, and holds between thresholds. Down and up
                // counts are independent so recovery can be judged more cautiously.
                if (rt.PingFailStreak >= rt.PingDownCount) rt.Healthy = false;
                else if (rt.PingOkStreak >= rt.PingUpCount) rt.Healthy = true;
            }
            else if (rt.Keepalive == 0 && IsStandbyEverywhere(tunnel, rt, groups))
            {
                // A keepalive-less STANDBY routes no traffic, so it can never handshake —
                // judging its handshake stale marked it permanently unhealthy, which
                // poisoned the whole group: when the active died, no member was "healthy"
                // and failover simply never happened. With no signal either way, assume
                // it's available; promotion (PromotedAt) gives it a real grace window to
                // prove itself, after which staleness applies again — so a dead standby
                // gets skipped on the NEXT hop instead of blocking the first one.
                rt.Healthy = true;
            }
            else
            {
                // Handshake freshness, with grace measured from the LATER of connect and
                // promotion — a just-promoted peer's pre-promotion handshake age means
                // nothing (it couldn't handshake while standby).
                var basis = rt.PromotedAt > rt.ConnectedAt ? rt.PromotedAt : rt.ConnectedAt;
                rt.Healthy = (rt.LastHandshake is { } h && now - h < HandshakeStale)
                    || now - basis < HandshakeGrace;
            }
        }
    }

    // Member of at least one overlap group, active in none of them, with no ungrouped
    // range of its own — i.e. a peer that cannot be carrying any traffic right now.
    bool IsStandbyEverywhere(ActiveTunnel tunnel, PeerRuntime rt,
        Dictionary<string, List<(ActiveTunnel Tunnel, PeerRuntime Peer)>> groups)
    {
        bool inAnyGroup = false;
        var key = MemberKey(tunnel, rt);
        foreach (var (cidr, members) in groups)
        {
            if (!members.Any(m => ReferenceEquals(m.Peer, rt))) continue;
            inAnyGroup = true;
            // Active here (or the group hasn't been arbitrated yet): judge it normally.
            if (!_groupActive.TryGetValue(cidr, out var active) || active == key) return false;
        }
        if (!inAnyGroup) return false;
        // Any range of its own that ISN'T contested still carries this peer's traffic.
        return rt.AllowedIps.All(a => groups.ContainsKey(CidrKey(a.Ip, a.Cidr)));
    }

    bool PingRoutesElsewhere(ActiveTunnel tunnel, PeerRuntime rt, IPAddress pingIp,
        Dictionary<string, List<(ActiveTunnel Tunnel, PeerRuntime Peer)>> groups)
    {
        if (rt.HasProbeRoute)
        {
            // The pin is only trustworthy while its /32 isn't itself an arbitrated range
            // owned by someone else — a covering tunnel's shadow route (or a third tunnel
            // claiming the exact /32) retunes the SAME row, silently repurposing the pin.
            var probeKey = CidrKey(pingIp, 32);
            return _groupActive.TryGetValue(probeKey, out var owner)
                && owner != MemberKey(tunnel, rt);
        }
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
    // Specific-range routes we created on adapters that only cover the range with a
    // broader route (subset failover) — ours to delete when the group dissolves.
    readonly HashSet<(ulong Luid, string Cidr)> _shadowRoutes = new();

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
        // Subset routes: a specific range (10.7.0.5/32) also groups with every peer whose
        // broader route covers it (10.7.0.0/24) — the group key stays the specific range,
        // and the covering peers become failover candidates for it.
        foreach (var key in groups.Keys.ToList())
            foreach (var (t, p) in all)
            {
                if (groups[key].Any(m => ReferenceEquals(m.Peer, p))) continue;
                if (p.AllowedIps.Any(a => CoversKey(a, key))) groups[key].Add((t, p));
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
                // Fresh promotion: restart the handshake grace clock and drop any streaks
                // banked before promotion — the first post-promotion verdict must come
                // from probes that traverse the newly-active path.
                active.Peer.PromotedAt = now;
                active.Peer.PingFailStreak = 0;
                active.Peer.PingOkStreak = 0;
                if (prevActive is not null && groups.Count > 0)
                    FailoverChanged?.Invoke($"{key}: {(from == active.Tunnel.Name ? "peer switch" : $"failover {from} → {active.Tunnel.Name}")}");
            }

            // Route metrics: the active member's adapter wins; every other adapter in the
            // group is pushed far back. Adapters keep one route per CIDR regardless of how
            // many of their peers claim it. Adapters that only cover the range with a
            // broader route get a shadow route for it — prefix length beats metric, so a
            // covering peer can never win against a live specific route without one.
            uint standby = StandbyMetricBase;
            foreach (var adapterMembers in ordered.GroupBy(m => m.Tunnel))
            {
                var t = adapterMembers.Key;
                var metric = ReferenceEquals(t, active.Tunnel) ? 0u : standby += 16;
                var claimants = adapterMembers
                    .Where(m => m.Peer.AllowedIps.Any(a => CidrKey(a.Ip, a.Cidr) == key))
                    .ToList();
                if (claimants.Count > 0) ApplyMetric(t, key, metric);
                else EnsureShadowRoute(t, key, metric);

                // Within one adapter the CIDR must live on exactly one peer's WG config:
                // the group's active peer if it's here (a covering owner simply takes the
                // range away from its unhealthy claimants — WireGuard's cryptokey routing
                // then falls through to the broader route), else the best healthy claimant.
                var owner = ReferenceEquals(t, active.Tunnel)
                    ? active.Peer
                    : (claimants.FirstOrDefault(m => m.Peer.Healthy).Peer ?? claimants.FirstOrDefault().Peer);
                if (owner is not null && claimants.Count > 0 && adapterMembers.Count() > 1 &&
                    (!t.IntraOwner.TryGetValue(key, out var cur) || cur != owner.Key))
                {
                    t.IntraOwner[key] = owner.Key;
                    wgDirty.Add(t);
                }
            }
        }

        // Groups that dissolved (disconnect / config change): drop state, delete any
        // shadow routes we created, and restore the surviving route — if any — to the
        // default metric so it isn't stuck on standby.
        foreach (var stale in _groupActive.Keys.Where(k => !groups.ContainsKey(k)).ToList())
        {
            _groupActive.Remove(stale);
            foreach (var t in snapshot)
            {
                _appliedMetric.Remove((t.Adapter.Luid, stale));
                if (_shadowRoutes.Remove((t.Adapter.Luid, stale))
                    && WireGuardConf.TryParseCidr(stale, out var sip, out var spfx))
                    try { Netio.DeleteRoute(t.Adapter.Luid, sip, (byte)spfx); } catch { }
                else if (t.Peers.Any(p => p.AllowedIps.Any(a => CidrKey(a.Ip, a.Cidr) == stale)))
                    ApplyMetric(t, stale, 0);
            }
        }
        // Purge per-adapter state for adapters that no longer exist.
        var liveLuids = snapshot.Select(t => t.Adapter.Luid).ToHashSet();
        foreach (var k in _appliedMetric.Keys.Where(k => !liveLuids.Contains(k.Luid)).ToList())
            _appliedMetric.Remove(k);
        _shadowRoutes.RemoveWhere(k => !liveLuids.Contains(k.Luid));

        foreach (var t in wgDirty)
        {
            try { t.Adapter.SetConfiguration(t.PrivateKey, t.ListenPort, BuildWgPeers(t)); }
            catch { }
        }
    }

    // Create-or-retune the specific route on an adapter whose peer only covers the range
    // with a broader one. Kept in _shadowRoutes so dissolution can delete it.
    void EnsureShadowRoute(ActiveTunnel tunnel, string groupCidr, uint metric)
    {
        var stateKey = (tunnel.Adapter.Luid, groupCidr);
        if (_appliedMetric.TryGetValue(stateKey, out var cur) && cur == metric && _shadowRoutes.Contains(stateKey))
            return;
        if (!WireGuardConf.TryParseCidr(groupCidr, out var ip, out var prefix)) return;
        try
        {
            var r = Netio.TrySetRouteMetric(tunnel.Adapter.Luid, ip, (byte)prefix, metric);
            if (r == Netio.RouteMetric.Failed) return; // don't record: retry next tick
            if (r == Netio.RouteMetric.Missing)
                Netio.AddRoute(tunnel.Adapter.Luid, ip, (byte)prefix, metric: metric);
            _shadowRoutes.Add(stateKey);
            _appliedMetric[stateKey] = metric;
        }
        catch { } // adapter tearing down: reconcile retries next tick
    }

    void ApplyMetric(ActiveTunnel tunnel, string groupCidr, uint metric)
    {
        var stateKey = (tunnel.Adapter.Luid, groupCidr);
        if (_appliedMetric.TryGetValue(stateKey, out var cur) && cur == metric) return;
        if (!WireGuardConf.TryParseCidr(groupCidr, out var ip, out var prefix)) return;
        // Cache ONLY after every syscall succeeded: recording intent before the set (and
        // discarding failures) froze routes at stale metrics forever — the reconcile diff
        // believed the state converged while traffic still followed the dead tunnel. A
        // failed pass simply retries next tick; a route deleted externally is recreated.
        bool ok;
        if (prefix == 0)
        {
            // A /0 group is installed as the two /1 halves; both must land.
            ok = ip.AddressFamily == AddressFamily.InterNetwork
                ? SetOrRecreate(IPAddress.Parse("0.0.0.0"), 1) & SetOrRecreate(IPAddress.Parse("128.0.0.0"), 1)
                : SetOrRecreate(IPAddress.Parse("::"), 1) & SetOrRecreate(IPAddress.Parse("8000::"), 1);
        }
        else ok = SetOrRecreate(ip, (byte)prefix);
        if (ok) _appliedMetric[stateKey] = metric;

        bool SetOrRecreate(IPAddress dst, byte pfx)
        {
            try
            {
                switch (Netio.TrySetRouteMetric(tunnel.Adapter.Luid, dst, pfx, metric))
                {
                    case Netio.RouteMetric.Applied: return true;
                    case Netio.RouteMetric.Missing:
                        Netio.AddRoute(tunnel.Adapter.Luid, dst, pfx, metric: metric);
                        return true;
                    default: return false;
                }
            }
            catch { return false; }
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
