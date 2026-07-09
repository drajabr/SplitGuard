using System.Collections.ObjectModel;
using Avalonia.Threading;
using SplitGuard.Models;
using SplitGuard.Services;

namespace SplitGuard.ViewModels;

public interface IDialogs
{
    Task CopyToClipboardAsync(string text);
    void Notify(string title, string message, bool isError);
}

public class MainViewModel : ObservableObject, ITunnelHost
{
    readonly IDialogs _dialogs;
    readonly RuleStore _store = new();
    readonly NrptService _nrpt = new();
    readonly TunnelManager _tunnels = new();
    readonly TunnelService _external = new();
    AppConfig _config = new();

    public ObservableCollection<TunnelViewModel> Tunnels { get; } = new();

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        _config = _store.Load(); // sync, so UI prefs are available before the window shows
        _tunnels.StatsUpdated += (name, stats) => Dispatcher.UIThread.Post(() =>
        {
            var vm = Tunnels.FirstOrDefault(t => !t.IsExternal && t.Name == name);
            if (vm is null) return;
            var wasEstablished = vm.IsEstablished;
            vm.ApplyStats(stats);
            // "Connected" means a handshake completed — only announce it then.
            if (!wasEstablished && vm.IsEstablished)
            {
                Notify(vm.Name, "Connected", false);
                NotifyStatus();
            }
        });
        _tunnels.FailoverChanged += msg => Dispatcher.UIThread.Post(() =>
        {
            StatusText = msg;
            StatusOk = true;
            Notify("Failover", msg, false);
        });
        _external.AdaptersChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshExternalsAsync());
        Tunnels.CollectionChanged += (_, _) => NotifyStatus();
    }

    // ---- bottom status bar ------------------------------------------------------

    public string TunnelSummary
    {
        get
        {
            var all = Tunnels.Where(t => !t.IsCustom).ToList();
            return $"{all.Count(t => t.IsConnected)}/{all.Count} on";
        }
    }

    // Bottom-bar pin readout: which tunnel's DNS owns the device, and whether it's live.
    (bool Pinned, string Text) DnsState()
    {
        if (_config.PinnedDns is null) return (false, "System DNS");
        foreach (var t in Tunnels)
            foreach (var p in t.Peers.Where(p => p.HasDns))
                if (_config.PinnedDns.TunnelName == TunnelKey(t) && _config.PinnedDns.PeerPublicKey == PeerKey(t, p))
                    return t.IsConnected
                        ? (true, $"Device DNS pinned · {t.Name} · {p.Dns.Trim()}")
                        : (true, $"Pin suspended ({t.Name} off) · System DNS");
        return (false, "System DNS");
    }

    public string DnsStatus => DnsState().Text;
    public bool DnsPinned => DnsState().Pinned;

    void NotifyStatus()
    {
        Raise(nameof(TunnelSummary));
        Raise(nameof(DnsStatus));
        Raise(nameof(DnsPinned));
    }

    public UiPrefs Prefs => _config.Ui;
    public void PersistPrefs() => _store.Save(_config);

    bool _gpoWarning;
    public bool GpoWarning { get => _gpoWarning; set => Set(ref _gpoWarning, value); }

    string _statusText = "";
    int _statusVersion;
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (!Set(ref _statusText, value)) return;
            Raise(nameof(HasStatus));
            // Auto-hide transient messages after a few seconds so the bar doesn't linger.
            var v = ++_statusVersion;
            if (value.Length > 0) _ = ClearStatusLater(v);
        }
    }
    public bool HasStatus => StatusText.Length > 0;

    async Task ClearStatusLater(int version)
    {
        await Task.Delay(7000);
        if (version == _statusVersion) StatusText = "";
    }

    bool _statusOk;
    public bool StatusOk { get => _statusOk; set => Set(ref _statusOk, value); }

    // Fire an OS/in-app notification when the user has them enabled.
    void Notify(string title, string message, bool isError)
    {
        if (_config.Ui.Notifications) _dialogs.Notify(title, message, isError);
    }

    public void AccentChanged(TunnelViewModel tunnel) => _store.Save(_config);

    // A tunnel refused to turn on because its config is incomplete/invalid.
    public void ReportError(TunnelViewModel tunnel, string message)
    {
        StatusText = $"{tunnel.Name}: {message}";
        StatusOk = false;
        Notify(tunnel.Name, message, true);
    }

    // Custom DNS card's activate/deactivate button: apply or remove its NRPT rules.
    public void CustomActiveChanged(TunnelViewModel tunnel)
    {
        _store.Save(_config);
        _ = Task.Run(() =>
        {
            if (_config.Custom?.Active == true) ApplyCustomRules(tunnel);
            else _nrpt.RemoveByTunnel(CustomName);
            RefreshCatchAll();
        });
        NotifyStatus();
    }

    public async Task InitializeAsync()
    {
        GpoWarning = NrptService.IsGpoNrptActive();
        foreach (var t in _config.Tunnels)
            Tunnels.Add(new TunnelViewModel(this, t));
        // Custom DNS forwarding is on by default; materialize its card if enabled.
        if (_config.Ui.CustomDnsEnabled && _config.Custom is null)
        {
            _config.Custom = new CustomDnsConfig();
            _store.Save(_config);
        }
        if (_config.Custom is not null)
            Tunnels.Add(new TunnelViewModel(this, _config.Custom));
        await RefreshExternalsAsync();
        SortTunnels();
        await Task.Run(Reconcile);
        RefreshPins();
        await Task.Run(RefreshCatchAll);
    }

    // Startup crash recovery: drop tagged rules that no longer correspond to anything live.
    void Reconcile()
    {
        try
        {
            var tagged = _nrpt.GetTaggedRules();
            var desired = DesiredExternalRuleIds();
            var stale = tagged.Where(r => !desired.Contains(r.Id)).Select(r => r.Id).ToList();
            if (stale.Count > 0) _nrpt.RemoveTagged(stale);
            foreach (var ext in _config.Externals)
            {
                var vm = Tunnels.FirstOrDefault(t => t.IsExternal && t.Name == ext.AdapterName);
                if (vm is null || !vm.IsConnected || string.IsNullOrEmpty(ext.Dns)) continue;
                foreach (var d in ext.Domains)
                {
                    var id = NrptService.RuleId(ExtName(ext.AdapterName), ext.AdapterName, d);
                    if (!tagged.Any(r => r.Id == id))
                        _nrpt.ApplyDomain(ExtName(ext.AdapterName), ext.AdapterName, d, ext.Dns);
                }
            }
            // Re-apply any missing custom-card rules (only while active).
            if (_config.Custom is { Active: true })
                foreach (var role in _config.Custom.Roles.Where(r => !string.IsNullOrEmpty(r.Dns)))
                    foreach (var d in role.Domains)
                    {
                        var id = NrptService.RuleId(CustomName, role.Id, d);
                        if (!tagged.Any(r => r.Id == id))
                            _nrpt.ApplyDomain(CustomName, role.Id, d, role.Dns!);
                    }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { StatusText = $"NRPT reconcile failed: {ex.Message}"; StatusOk = false; });
        }
    }

    HashSet<string> DesiredExternalRuleIds()
    {
        var set = new HashSet<string>();
        foreach (var ext in _config.Externals)
        {
            var vm = Tunnels.FirstOrDefault(t => t.IsExternal && t.Name == ext.AdapterName);
            if (vm is null || !vm.IsConnected || string.IsNullOrEmpty(ext.Dns)) continue;
            foreach (var d in ext.Domains)
                set.Add(NrptService.RuleId(ExtName(ext.AdapterName), ext.AdapterName, d));
        }
        // Custom card rules are desired while the card is active.
        if (_config.Custom is { Active: true })
            foreach (var role in _config.Custom.Roles.Where(r => !string.IsNullOrEmpty(r.Dns)))
                foreach (var d in role.Domains)
                    set.Add(NrptService.RuleId(CustomName, role.Id, d));
        return set;
    }

    static string ExtName(string adapter) => $"ext:{adapter}";
    const string CustomName = "custom:dns";

    // NRPT/pin identity for a card: externals prefix the adapter, the custom card uses a
    // fixed sentinel, WG tunnels use their name. Peer identity is the peer's public key
    // (for externals there's one implicit peer keyed by the adapter name).
    string TunnelKey(TunnelViewModel t) => t.IsExternal ? ExtName(t.Name) : t.IsCustom ? CustomName : t.Name;
    static string PeerKey(TunnelViewModel t, PeerViewModel p) => t.IsExternal ? t.Name : p.PublicKey;

    // ---- tunnel lifecycle -------------------------------------------------

    public void RequestConnect(TunnelViewModel vm) => _ = Task.Run(() =>
    {
        try
        {
            _tunnels.Connect(vm.Config!);
            foreach (var p in vm.Config!.Peers.Where(p => p.Dns is not null && p.Domains.Count > 0))
                _nrpt.ApplyPeerRules(vm.Name, p.PublicKey, p.Domains, p.Dns!);
            RefreshCatchAll();
            // No "Connected" announcement yet — that fires on the first handshake.
            Dispatcher.UIThread.Post(() => { StatusText = ""; RefreshPins(); });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                vm.SetConnectedState(false);
                StatusText = $"{vm.Name}: {ex.Message}";
                StatusOk = false;
                Notify(vm.Name, $"Connection failed: {ex.Message}", true);
            });
        }
    });

    public void RequestDisconnect(TunnelViewModel vm) => _ = Task.Run(() =>
    {
        try
        {
            foreach (var p in vm.Config!.Peers)
                _nrpt.RemovePeerRules(vm.Name, p.PublicKey);
            _tunnels.Disconnect(vm.Name);
            RefreshCatchAll();
            Dispatcher.UIThread.Post(() => { RefreshPins(); Notify(vm.Name, "Disconnected", false); });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"{vm.Name}: {ex.Message}";
                StatusOk = false;
                Notify(vm.Name, $"Disconnect failed: {ex.Message}", true);
            });
        }
    });

    // ---- pin / catch-all --------------------------------------------------

    public void TogglePin(TunnelViewModel tunnel, PeerViewModel peer)
    {
        if (peer.IsPinned)
            _config.PinnedDns = null;
        else if (peer.HasDns)
            _config.PinnedDns = new PinnedDnsRef
            {
                TunnelName = TunnelKey(tunnel),
                PeerPublicKey = PeerKey(tunnel, peer),
            };
        _store.Save(_config);
        RefreshPins();
        _ = Task.Run(RefreshCatchAll);
    }

    void RefreshPins()
    {
        foreach (var t in Tunnels)
        {
            foreach (var p in t.Peers)
            {
                var pinned = _config.PinnedDns is not null
                    && _config.PinnedDns.TunnelName == TunnelKey(t)
                    && _config.PinnedDns.PeerPublicKey == PeerKey(t, p);
                p.IsPinned = pinned;
                p.PinSuspended = pinned && !t.IsConnected;
            }
            t.NotifyPresentation();
        }
        NotifyStatus();
    }

    // The single catch-all rule: pinned server first, then other live tunnel DNS, then
    // system DNS. Called from background threads, so the view-model collections are read
    // via a UI-thread snapshot; only the NRPT I/O runs on the calling background thread.
    void RefreshCatchAll()
    {
        try
        {
            var tunnelServers = Dispatcher.UIThread.Invoke(SnapshotPinnedChain);
            if (tunnelServers is null) { _nrpt.RemoveCatchAll(); return; }
            var servers = new List<string>(tunnelServers);
            servers.AddRange(SystemDns.Snapshot());
            var chain = servers.Distinct().ToArray();
            if (chain.Length == 0) _nrpt.RemoveCatchAll();
            else _nrpt.SetCatchAll(chain);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { StatusText = $"Device DNS update failed: {ex.Message}"; StatusOk = false; });
        }
    }

    // UI-thread only: ordered connected-peer DNS, or null when nothing is pinned.
    // Order: the explicitly pinned server first, then by priority custom → external →
    // our tunnels (RefreshCatchAll de-duplicates while preserving this order).
    List<string>? SnapshotPinnedChain()
    {
        if (_config.PinnedDns is null) return null;
        var servers = new List<string>();

        // The pinned peer wins outright.
        foreach (var t in Tunnels.Where(t => t.IsConnected))
            foreach (var p in t.Peers.Where(p => p.HasDns))
                if (_config.PinnedDns.TunnelName == TunnelKey(t) && _config.PinnedDns.PeerPublicKey == PeerKey(t, p))
                    servers.Add(p.Dns.Trim());

        void AddFrom(Func<TunnelViewModel, bool> category)
        {
            foreach (var t in Tunnels.Where(t => t.IsConnected && category(t)))
                foreach (var p in t.Peers.Where(p => p.HasDns))
                    servers.Add(p.Dns.Trim());
        }
        AddFrom(t => t.IsCustom);
        AddFrom(t => t.IsExternal);
        AddFrom(t => !t.IsCustom && !t.IsExternal);
        return servers;
    }

    public void EditStarted(TunnelViewModel tunnel)
    {
        foreach (var other in Tunnels.Where(t => t.IsEditing && !ReferenceEquals(t, tunnel)).ToList())
            other.CancelEditCommand.Execute(null);
    }

    public bool IsDomainInUse(string domain, PeerViewModel except) =>
        Tunnels.SelectMany(t => t.Peers)
            .Where(p => !ReferenceEquals(p, except))
            .SelectMany(p => p.DomainValues)
            .Contains(domain, StringComparer.OrdinalIgnoreCase);

    // ---- route-group metrics -------------------------------------------------

    IEnumerable<PeerViewModel> WgPeerVms() =>
        Tunnels.Where(t => !t.IsExternal && !t.IsCustom).SelectMany(t => t.Peers);

    static bool SharesCidr(PeerViewModel a, PeerViewModel b) =>
        a.AllowedIpValues.Select(WireGuardConf.CanonicalCidr)
            .Intersect(b.AllowedIpValues.Select(WireGuardConf.CanonicalCidr)).Any();

    // A route group with duplicate metrics can't be arbitrated; blocks connect.
    public string? MetricConflict(TunnelViewModel tunnel)
    {
        foreach (var p in tunnel.Peers)
        {
            var clash = WgPeerVms().FirstOrDefault(o => !ReferenceEquals(o, p) && o.ParsedMetric == p.ParsedMetric && SharesCidr(o, p));
            if (clash is not null)
                return $"Overlapping allowed IPs share metric {p.ParsedMetric} — give each peer in the group a distinct metric";
        }
        return null;
    }

    // ---- edit/save/delete ---------------------------------------------------

    // Overlapping allowed IPs (same CIDR on several peers/tunnels) are legal — that's the
    // failover feature — but health judgment needs keepalive or a ping host on each member.
    string? OverlapHealthWarning()
    {
        var claims = new Dictionary<string, List<(string Tunnel, PeerConfig Peer)>>();
        foreach (var t in _config.Tunnels)
            foreach (var p in t.Peers)
                foreach (var cidr in p.AllowedIps.Select(WireGuardConf.NormalizeCidr).Distinct())
                {
                    if (!claims.TryGetValue(cidr, out var list)) claims[cidr] = list = new();
                    list.Add((t.Name, p));
                }
        var weak = claims.Values.Where(members => members.Count > 1)
            .SelectMany(members => members)
            .Where(m => m.Peer.PersistentKeepalive == 0 && string.IsNullOrEmpty(m.Peer.PingHost))
            .Select(m => m.Tunnel)
            .Distinct()
            .ToList();
        return weak.Count == 0 ? null
            : $"Overlapping allowed IPs: set a keepalive or ping host on {string.Join(", ", weak)} so failover can judge health";
    }

    public void TunnelSaved(TunnelViewModel vm, bool connectionChanged)
    {
        _store.Save(_config);
        RefreshPins();
        if (!vm.IsCustom && !vm.IsExternal && OverlapHealthWarning() is { } overlapWarn)
        {
            StatusText = overlapWarn;
            StatusOk = false;
        }
        if (!vm.IsCustom && !vm.IsExternal && vm.IsConnected && connectionChanged)
            vm.MarkReconnecting();
        _ = Task.Run(() =>
        {
            if (vm.IsCustom)
            {
                if (vm.Custom!.Active) ApplyCustomRules(vm);
                else _nrpt.RemoveByTunnel(CustomName);
            }
            else if (vm.IsExternal)
            {
                if (vm.IsConnected) ReapplyExternalRules(vm);
            }
            else if (vm.IsConnected)
            {
                // DNS/domain-only edits refresh NRPT in place; connection edits need a quick reconnect.
                foreach (var p in vm.Config!.Peers) _nrpt.RemovePeerRules(vm.Name, p.PublicKey);
                if (connectionChanged) _tunnels.Disconnect(vm.Name);
                try
                {
                    if (connectionChanged) _tunnels.Connect(vm.Config);
                    foreach (var p in vm.Config.Peers.Where(p => p.Dns is not null && p.Domains.Count > 0))
                        _nrpt.ApplyPeerRules(vm.Name, p.PublicKey, p.Domains, p.Dns!);
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        vm.SetConnectedState(false);
                        StatusText = $"{vm.Name}: {ex.Message}";
                        StatusOk = false;
                    });
                }
            }
            RefreshCatchAll();
        });
    }

    // Rewrite all custom-card NRPT rules from scratch (they're always active while running).
    void ApplyCustomRules(TunnelViewModel vm)
    {
        _nrpt.RemoveByTunnel(CustomName);
        foreach (var p in vm.Peers.Where(p => p.HasDns && p.DomainValues.Any()))
            _nrpt.ApplyPeerRules(CustomName, p.PublicKey, p.DomainValues, p.Dns.Trim());
    }

    void ReapplyExternalRules(TunnelViewModel vm)
    {
        _nrpt.RemovePeerRules(ExtName(vm.Name), vm.Name);
        var ext = vm.External!;
        if (!string.IsNullOrEmpty(ext.Dns) && ext.Domains.Count > 0)
            _nrpt.ApplyPeerRules(ExtName(vm.Name), vm.Name, ext.Domains, ext.Dns);
    }

    public void RequestDelete(TunnelViewModel vm)
    {
        if (vm.IsConnected && !vm.IsExternal && !vm.IsCustom) RequestDisconnect(vm);
        if (_config.PinnedDns?.TunnelName == TunnelKey(vm))
            _config.PinnedDns = null;
        if (vm.IsCustom)
        {
            _config.Custom = null;
            _ = Task.Run(() => _nrpt.RemoveByTunnel(CustomName));
        }
        else if (vm.IsExternal)
        {
            _config.Externals.Remove(vm.External!);
            // Remember the dismissal so a rescan is required to bring it back.
            if (!_config.DismissedExternals.Contains(vm.Name)) _config.DismissedExternals.Add(vm.Name);
        }
        else _config.Tunnels.Remove(vm.Config!);
        _store.Save(_config);
        Tunnels.Remove(vm);
        _ = Task.Run(RefreshCatchAll);
    }

    // Settings toggle: the single standalone custom-DNS card. On → create it (top of the
    // list); off → remove it and its rules.
    public bool HasCustomDns => _config.Custom is not null;

    public void ToggleCustomDns(bool on)
    {
        _config.Ui.CustomDnsEnabled = on;
        if (on)
        {
            if (_config.Custom is not null) { _store.Save(_config); return; }
            _config.Custom = new CustomDnsConfig();
            _store.Save(_config);
            Tunnels.Add(new TunnelViewModel(this, _config.Custom));
            SortTunnels();
            Tunnels.FirstOrDefault(t => t.IsCustom)?.BeginEditCommand.Execute(null);
        }
        else
        {
            var vm = Tunnels.FirstOrDefault(t => t.IsCustom);
            if (vm is not null) RequestDelete(vm);
            _store.Save(_config);
        }
    }

    // Display/priority order: custom DNS first, then externals, then our tunnels.
    void SortTunnels()
    {
        var ordered = Tunnels.OrderBy(t => t.IsCustom ? 0 : t.IsExternal ? 1 : 2).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var cur = Tunnels.IndexOf(ordered[i]);
            if (cur != i) Tunnels.Move(cur, i);
        }
    }

    // Forget dismissed externals so the next scan re-adds any that are still present.
    public void RescanExternals()
    {
        _config.DismissedExternals.Clear();
        _store.Save(_config);
        _ = RefreshExternalsAsync();
    }

    public void CopyText(string text) => _ = _dialogs.CopyToClipboardAsync(text);

    // Ctrl+N: fresh keypair, one empty peer, straight into edit; cancel deletes it.
    public void CreateEmptyTunnel()
    {
        var name = "tunnel";
        for (int i = 2; _config.Tunnels.Any(t => t.Name == name); i++) name = $"tunnel-{i}";
        var cfg = new TunnelConfig
        {
            Name = name,
            PrivateKeyProtected = RuleStore.Protect(Convert.ToBase64String(Curve25519.GeneratePrivateKey())),
            Peers = new List<PeerConfig> { new() },
        };
        _config.Tunnels.Add(cfg);
        var vm = new TunnelViewModel(this, cfg) { IsDraft = true };
        Tunnels.Add(vm);
        vm.BeginEditCommand.Execute(null);
    }

    // ---- add tunnel (drag-drop or Ctrl+V) --------------------------------------

    public static bool LooksLikeConfig(string? text) =>
        text is not null && text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase);

    public void AddTunnelFromText(string text, string? suggestedName)
    {
        var parsed = WireGuardConf.Parse(text);
        if (!PeerViewModel.IsValidKey(parsed.PrivateKey))
        {
            StatusText = "Import failed: config has no valid PrivateKey";
            StatusOk = false;
            return;
        }
        var name = suggestedName ?? parsed.Name ?? "tunnel";
        var baseName = name;
        for (int i = 2; _config.Tunnels.Any(t => t.Name == name); i++) name = $"{baseName}-{i}";
        var dnsPeer = WireGuardConf.PeerForDns(parsed);
        var cfg = new TunnelConfig
        {
            Name = name,
            PrivateKeyProtected = RuleStore.Protect(parsed.PrivateKey),
            ListenPort = parsed.ListenPort,
            Addresses = parsed.Addresses.ToList(),
            Peers = parsed.Peers.Select(p => new PeerConfig
            {
                PublicKey = p.PublicKey,
                PresharedKeyProtected = p.PresharedKey is null ? null : RuleStore.Protect(p.PresharedKey),
                Endpoint = p.Endpoint,
                AllowedIps = p.AllowedIps.ToList(),
                PersistentKeepalive = p.PersistentKeepalive,
                // Prefer the peer's own DNS extension; fall back to the interface DNS on its peer.
                Dns = p.Dns ?? (ReferenceEquals(p, dnsPeer) ? parsed.InterfaceDns : null),
                Domains = p.Domains.ToList(),
                PingHost = p.PingHost,
                PingTimeout = p.PingTimeout,
                PingCount = p.PingCount,
                Metric = Math.Clamp(p.Metric, 0, 10),
            }).ToList(),
        };
        _config.Tunnels.Add(cfg);
        _store.Save(_config);
        Tunnels.Add(new TunnelViewModel(this, cfg));
        StatusText = $"Tunnel '{name}' added";
        StatusOk = true;
    }

    // ---- externals -------------------------------------------------------------

    async Task RefreshExternalsAsync()
    {
        var adapters = (await Task.Run(_external.GetExternalAdapters))
            .Where(a => _config.Tunnels.All(t => t.Name != a.Name)) // our own adapters aren't "external"
            .Where(a => !_config.DismissedExternals.Contains(a.Name)) // user-deleted until rescan
            .ToList();
        foreach (var adapter in adapters)
        {
            var ext = _config.Externals.FirstOrDefault(e => e.AdapterName == adapter.Name);
            if (ext is null)
            {
                ext = new ExternalRuleConfig { AdapterName = adapter.Name };
                _config.Externals.Add(ext);
                _store.Save(_config);
            }
            var vm = Tunnels.FirstOrDefault(t => t.IsExternal && t.Name == adapter.Name);
            if (vm is null)
            {
                vm = new TunnelViewModel(this, ext);
                Tunnels.Add(vm);
            }
            var wasUp = vm.IsConnected;
            vm.SetConnectedState(adapter.IsUp);
            if (wasUp != adapter.IsUp)
            {
                var captured = vm;
                _ = Task.Run(() =>
                {
                    if (adapter.IsUp) ReapplyExternalRules(captured);
                    else _nrpt.RemovePeerRules(ExtName(captured.Name), captured.Name);
                    RefreshCatchAll();
                });
            }
        }
        // Adapters that vanished (tunnel removed in the official client): mark down, keep config.
        foreach (var vm in Tunnels.Where(t => t.IsExternal && adapters.All(a => a.Name != t.Name)).ToList())
        {
            if (vm.IsConnected)
            {
                vm.SetConnectedState(false);
                var captured = vm;
                _ = Task.Run(() =>
                {
                    _nrpt.RemovePeerRules(ExtName(captured.Name), captured.Name);
                    RefreshCatchAll();
                });
            }
        }
        RefreshPins();
    }

    // ---- shutdown --------------------------------------------------------------------

    public void OnExit()
    {
        try
        {
            foreach (var vm in Tunnels.Where(t => !t.IsExternal && !t.IsCustom && t.IsConnected))
            {
                foreach (var p in vm.Config!.Peers)
                    _nrpt.RemovePeerRules(vm.Name, p.PublicKey);
            }
            if (_config.Custom is not null) _nrpt.RemoveByTunnel(CustomName);
            _tunnels.DisconnectAll();
            // Catch-all survives only if the pinned DNS belongs to an external tunnel that stays up.
            var pinnedExternalUp = _config.PinnedDns is not null
                && Tunnels.Any(t => t.IsExternal && t.IsConnected && ExtName(t.Name) == _config.PinnedDns.TunnelName);
            if (!pinnedExternalUp) _nrpt.RemoveCatchAll();
        }
        catch { }
    }
}
