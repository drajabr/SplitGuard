using System.Collections.ObjectModel;
using Avalonia.Threading;
using WgSplitDns.Models;
using WgSplitDns.Services;

namespace WgSplitDns.ViewModels;

public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> PickConfFileAsync();
    Task<string?> PasteConfigAsync();
    Task CopyToClipboardAsync(string text);
}

public record TestTarget(string Label, string? Server)
{
    public override string ToString() => Label;
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
    public ObservableCollection<TestTarget> TestTargets { get; } = new();

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        ImportFileCommand = new RelayCommand(() => _ = ImportFileAsync());
        PasteConfigCommand = new RelayCommand(() => _ = PasteConfigAsync());
        TestCommand = new RelayCommand(() => _ = RunTestAsync(), () => !string.IsNullOrWhiteSpace(TestHost));
        _tunnels.StatsUpdated += (name, stats) => Dispatcher.UIThread.Post(() =>
            Tunnels.FirstOrDefault(t => !t.IsExternal && t.Name == name)?.ApplyStats(stats));
        _external.AdaptersChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshExternalsAsync());
    }

    bool _gpoWarning;
    public bool GpoWarning { get => _gpoWarning; set => Set(ref _gpoWarning, value); }

    string _testHost = "";
    public string TestHost
    {
        get => _testHost;
        set { if (Set(ref _testHost, value)) TestCommand.RaiseCanExecuteChanged(); }
    }

    TestTarget? _selectedTestTarget;
    public TestTarget? SelectedTestTarget { get => _selectedTestTarget; set => Set(ref _selectedTestTarget, value); }

    string _testResult = "";
    public string TestResult { get => _testResult; set { if (Set(ref _testResult, value)) Raise(nameof(HasTestResult)); } }
    public bool HasTestResult => TestResult.Length > 0;

    bool _testOk;
    public bool TestOk { get => _testOk; set => Set(ref _testOk, value); }

    public RelayCommand ImportFileCommand { get; }
    public RelayCommand PasteConfigCommand { get; }
    public RelayCommand TestCommand { get; }

    public async Task InitializeAsync()
    {
        _config = _store.Load();
        GpoWarning = NrptService.IsGpoNrptActive();
        foreach (var t in _config.Tunnels)
            Tunnels.Add(new TunnelViewModel(this, t));
        await RefreshExternalsAsync();
        await Task.Run(Reconcile);
        RefreshPins();
        RebuildTestTargets();
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
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { TestResult = $"NRPT reconcile failed: {ex.Message}"; TestOk = false; });
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
        return set;
    }

    static string ExtName(string adapter) => $"ext:{adapter}";

    // ---- tunnel lifecycle -------------------------------------------------

    public void RequestConnect(TunnelViewModel vm) => _ = Task.Run(() =>
    {
        try
        {
            _tunnels.Connect(vm.Config!);
            foreach (var p in vm.Config!.Peers.Where(p => p.Dns is not null && p.Domains.Count > 0))
                _nrpt.ApplyPeerRules(vm.Name, p.PublicKey, p.Domains, p.Dns!);
            RefreshCatchAll();
            Dispatcher.UIThread.Post(() => { vm.StatusError = ""; RefreshPins(); });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                vm.SetConnectedState(false);
                vm.StatusError = ex.Message;
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
            Dispatcher.UIThread.Post(RefreshPins);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => vm.StatusError = ex.Message);
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
                TunnelName = tunnel.IsExternal ? ExtName(tunnel.Name) : tunnel.Name,
                PeerPublicKey = tunnel.IsExternal ? tunnel.Name : peer.PublicKey,
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
                    && _config.PinnedDns.TunnelName == (t.IsExternal ? ExtName(t.Name) : t.Name)
                    && _config.PinnedDns.PeerPublicKey == (t.IsExternal ? t.Name : p.PublicKey);
                p.IsPinned = pinned;
                p.PinSuspended = pinned && !t.IsConnected;
            }
        }
    }

    // The single catch-all rule: pinned server first, then other live tunnel DNS, then system DNS.
    void RefreshCatchAll()
    {
        try
        {
            if (_config.PinnedDns is null)
            {
                _nrpt.RemoveCatchAll();
                return;
            }
            var servers = new List<string>();
            foreach (var t in Tunnels.Where(t => t.IsConnected))
            {
                foreach (var p in t.Peers.Where(p => p.HasDns))
                {
                    var isPinnedPeer = _config.PinnedDns.TunnelName == (t.IsExternal ? ExtName(t.Name) : t.Name)
                        && _config.PinnedDns.PeerPublicKey == (t.IsExternal ? t.Name : p.PublicKey);
                    if (isPinnedPeer) servers.Insert(0, p.Dns.Trim());
                    else servers.Add(p.Dns.Trim());
                }
            }
            servers.AddRange(SystemDns.Snapshot());
            var chain = servers.Distinct().ToArray();
            if (chain.Length == 0) _nrpt.RemoveCatchAll();
            else _nrpt.SetCatchAll(chain);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { TestResult = $"Device DNS update failed: {ex.Message}"; TestOk = false; });
        }
    }

    // ---- live domain edits (view mode) -------------------------------------

    public void DomainAdded(TunnelViewModel tunnel, PeerViewModel peer, string domain)
    {
        SyncPeerDomains(tunnel, peer);
        _store.Save(_config);
        RebuildTestTargets();
        if (tunnel.IsConnected && peer.HasDns)
        {
            var (name, key) = RuleKeyFor(tunnel, peer);
            _ = Task.Run(() => _nrpt.ApplyDomain(name, key, domain, peer.Dns.Trim()));
        }
    }

    public void DomainRemoved(TunnelViewModel tunnel, PeerViewModel peer, string domain)
    {
        SyncPeerDomains(tunnel, peer);
        _store.Save(_config);
        RebuildTestTargets();
        var (name, key) = RuleKeyFor(tunnel, peer);
        _ = Task.Run(() => _nrpt.RemoveDomain(name, key, domain));
    }

    static (string Name, string Key) RuleKeyFor(TunnelViewModel tunnel, PeerViewModel peer) =>
        tunnel.IsExternal ? (ExtName(tunnel.Name), tunnel.Name) : (tunnel.Name, peer.PublicKey);

    void SyncPeerDomains(TunnelViewModel tunnel, PeerViewModel peer)
    {
        if (tunnel.IsExternal)
        {
            tunnel.External!.Domains = peer.Domains.ToList();
        }
        else
        {
            var cfg = tunnel.Config!.Peers.FirstOrDefault(p => p.PublicKey == peer.PublicKey);
            if (cfg is not null) cfg.Domains = peer.Domains.ToList();
        }
    }

    public bool IsDomainInUse(string domain, PeerViewModel except) =>
        Tunnels.SelectMany(t => t.Peers)
            .Where(p => !ReferenceEquals(p, except))
            .SelectMany(p => p.Domains)
            .Contains(domain, StringComparer.OrdinalIgnoreCase);

    // ---- edit/save/delete ---------------------------------------------------

    public void TunnelSaved(TunnelViewModel vm)
    {
        _store.Save(_config);
        RefreshPins();
        RebuildTestTargets();
        _ = Task.Run(() =>
        {
            if (vm.IsExternal)
            {
                if (vm.IsConnected) ReapplyExternalRules(vm);
            }
            else if (vm.IsConnected)
            {
                // Connection fields may have changed: quick reapply (sub-second blip).
                foreach (var p in vm.Config!.Peers) _nrpt.RemovePeerRules(vm.Name, p.PublicKey);
                _tunnels.Disconnect(vm.Name);
                try
                {
                    _tunnels.Connect(vm.Config);
                    foreach (var p in vm.Config.Peers.Where(p => p.Dns is not null && p.Domains.Count > 0))
                        _nrpt.ApplyPeerRules(vm.Name, p.PublicKey, p.Domains, p.Dns!);
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => { vm.SetConnectedState(false); vm.StatusError = ex.Message; });
                }
            }
            RefreshCatchAll();
        });
    }

    void ReapplyExternalRules(TunnelViewModel vm)
    {
        _nrpt.RemovePeerRules(ExtName(vm.Name), vm.Name);
        var ext = vm.External!;
        if (!string.IsNullOrEmpty(ext.Dns) && ext.Domains.Count > 0)
            _nrpt.ApplyPeerRules(ExtName(vm.Name), vm.Name, ext.Domains, ext.Dns);
    }

    public async void RequestDelete(TunnelViewModel vm)
    {
        if (!await _dialogs.ConfirmAsync("Delete tunnel", $"Delete '{vm.Name}' and its DNS rules? This cannot be undone."))
            return;
        if (vm.IsConnected && !vm.IsExternal) RequestDisconnect(vm);
        if (_config.PinnedDns?.TunnelName == (vm.IsExternal ? ExtName(vm.Name) : vm.Name))
            _config.PinnedDns = null;
        if (vm.IsExternal) _config.Externals.Remove(vm.External!);
        else _config.Tunnels.Remove(vm.Config!);
        _store.Save(_config);
        Tunnels.Remove(vm);
        RebuildTestTargets();
        _ = Task.Run(RefreshCatchAll);
    }

    public void CopyText(string text) => _ = _dialogs.CopyToClipboardAsync(text);

    // ---- add tunnel -----------------------------------------------------------

    async Task ImportFileAsync()
    {
        var text = await _dialogs.PickConfFileAsync();
        if (text is not null) AddTunnelFromText(text.Split('\0')[0], text.Contains('\0') ? text.Split('\0')[1] : null);
    }

    async Task PasteConfigAsync()
    {
        var text = await _dialogs.PasteConfigAsync();
        if (text is not null) AddTunnelFromText(text, null);
    }

    void AddTunnelFromText(string text, string? suggestedName)
    {
        var parsed = WireGuardConf.Parse(text);
        if (!PeerViewModel.IsValidKey(parsed.PrivateKey))
        {
            TestResult = "Import failed: config has no valid PrivateKey";
            TestOk = false;
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
            Addresses = parsed.Addresses.ToList(),
            Peers = parsed.Peers.Select(p => new PeerConfig
            {
                PublicKey = p.PublicKey,
                PresharedKeyProtected = p.PresharedKey is null ? null : RuleStore.Protect(p.PresharedKey),
                Endpoint = p.Endpoint,
                AllowedIps = p.AllowedIps.ToList(),
                PersistentKeepalive = p.PersistentKeepalive,
                Dns = ReferenceEquals(p, dnsPeer) ? parsed.InterfaceDns : null,
            }).ToList(),
        };
        _config.Tunnels.Add(cfg);
        _store.Save(_config);
        Tunnels.Add(new TunnelViewModel(this, cfg));
        RebuildTestTargets();
    }

    // ---- externals -------------------------------------------------------------

    async Task RefreshExternalsAsync()
    {
        var adapters = await Task.Run(_external.GetExternalAdapters);
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
        RebuildTestTargets();
    }

    // ---- test bar -----------------------------------------------------------------

    void RebuildTestTargets()
    {
        var previous = SelectedTestTarget?.Label;
        TestTargets.Clear();
        TestTargets.Add(new TestTarget("Auto (effective)", null));
        foreach (var t in Tunnels)
        {
            var withDns = t.Peers.Where(p => p.HasDns).ToList();
            for (int i = 0; i < withDns.Count; i++)
            {
                var label = withDns.Count == 1 ? $"{t.Name} — {withDns[i].Dns}" : $"{t.Name} (peer {i + 1}) — {withDns[i].Dns}";
                TestTargets.Add(new TestTarget(label, withDns[i].Dns.Trim()));
            }
        }
        TestTargets.Add(new TestTarget("System DNS", "system"));
        SelectedTestTarget = TestTargets.FirstOrDefault(t => t.Label == previous) ?? TestTargets[0];
    }

    async Task RunTestAsync()
    {
        var host = TestHost.Trim();
        var target = SelectedTestTarget ?? TestTargets[0];
        TestResult = "testing…";
        TestOk = true;
        TestResult2(await RunTargetAsync(host, target));
    }

    async Task<TestResult> RunTargetAsync(string host, TestTarget target)
    {
        if (target.Server is null)
            return await TestService.ResolveAutoAsync(host);
        if (target.Server == "system")
        {
            var server = SystemDns.Snapshot().FirstOrDefault();
            if (server is null) return new TestResult(false, "No system DNS server found");
            return await TestService.ResolveDirectAsync(host, server, "system");
        }
        var label = target.Label.Contains(" — ") ? target.Label[..target.Label.IndexOf(" — ", StringComparison.Ordinal)] : target.Label;
        return await TestService.ResolveDirectAsync(host, target.Server, label);
    }

    void TestResult2(TestResult result)
    {
        TestOk = result.Success;
        TestResult = result.Message;
    }

    // ---- shutdown --------------------------------------------------------------------

    public void OnExit()
    {
        try
        {
            foreach (var vm in Tunnels.Where(t => !t.IsExternal && t.IsConnected))
            {
                foreach (var p in vm.Config!.Peers)
                    _nrpt.RemovePeerRules(vm.Name, p.PublicKey);
            }
            _tunnels.DisconnectAll();
            // Catch-all survives only if the pinned DNS belongs to an external tunnel that stays up.
            var pinnedExternalUp = _config.PinnedDns is not null
                && Tunnels.Any(t => t.IsExternal && t.IsConnected && ExtName(t.Name) == _config.PinnedDns.TunnelName);
            if (!pinnedExternalUp) _nrpt.RemoveCatchAll();
        }
        catch { }
    }
}
