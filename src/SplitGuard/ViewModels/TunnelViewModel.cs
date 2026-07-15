using System.Collections.ObjectModel;
using SplitGuard.Models;
using SplitGuard.Services;

namespace SplitGuard.ViewModels;

public interface ITunnelHost
{
    void RequestConnect(TunnelViewModel tunnel);
    void RequestDisconnect(TunnelViewModel tunnel);
    // Persist the tunnel's on/off intent (TunnelConfig.Connected) for startup restore.
    void PersistConnectedState(TunnelViewModel tunnel);
    void TogglePin(TunnelViewModel tunnel, PeerViewModel peer);
    bool IsDomainInUse(string domain, PeerViewModel except);
    // Whether this peer's allowed IPs overlap another peer's (metric is moot otherwise).
    bool HasRouteGroup(PeerViewModel peer);
    // The peer's failover standing: (position by metric, group size, shared CIDR), or
    // null when its allowed IPs overlap nobody.
    (int Position, int Size, string Cidr)? RouteGroupInfo(PeerViewModel peer);
    // This peer's own allowed-IP ranges (canonical) that overlap another peer's — the
    // failover routes it competes for.
    IReadOnlyList<string> RouteGroupCidrs(PeerViewModel peer);
    // Overlapping peers with equal metrics (a route group can't arbitrate them).
    string? MetricConflict(TunnelViewModel tunnel);
    // Force every route group to distinct metrics (auto-resolve duplicates, default blanks).
    void ReconcileMetrics();
    void EditStarted(TunnelViewModel tunnel);
    void TunnelSaved(TunnelViewModel tunnel, bool connectionChanged);
    void RequestDelete(TunnelViewModel tunnel);
    void CopyText(string text);
    void CustomActiveChanged(TunnelViewModel tunnel);
    void ReportError(TunnelViewModel tunnel, string message);
    // Drag-to-reorder: how many user tunnels exist, a live move step, and the drop-time save.
    int ReorderableCount { get; }
    void MoveTunnel(TunnelViewModel tunnel, int toIndex);
    void SaveTunnelOrder();
}

public class TunnelViewModel : ObservableObject
{
    public ITunnelHost Host { get; }
    public TunnelConfig? Config { get; }
    public ExternalRuleConfig? External { get; }
    public CustomDnsConfig? Custom { get; }

    public bool IsExternal => External is not null;
    public bool IsCustom => Custom is not null;

    public TunnelViewModel(ITunnelHost host, TunnelConfig config)
    {
        Host = host;
        Config = config;
        InitCommands();
        LoadFromConfig();
    }

    public TunnelViewModel(ITunnelHost host, ExternalRuleConfig external)
    {
        Host = host;
        External = external;
        InitCommands();
        Name = external.AdapterName;
        var peer = new PeerViewModel(this) { Dns = external.Dns ?? "" };
        PeerViewModel.FillDomains(peer.Domains, external.Domains);
        Peers.Add(peer);
    }

    public TunnelViewModel(ITunnelHost host, CustomDnsConfig custom)
    {
        Host = host;
        Custom = custom;
        InitCommands();
        Name = "Custom DNS";
        _isConnected = custom.Active; // active = its NRPT rules are applied
        if (custom.Roles.Count == 0) custom.Roles.Add(new CustomDnsRole());
        foreach (var role in custom.Roles)
        {
            var peer = new PeerViewModel(this) { PublicKey = role.Id, Dns = role.Dns ?? "" };
            PeerViewModel.FillDomains(peer.Domains, role.Domains);
            Peers.Add(peer);
        }
    }

    void InitCommands()
    {
        BeginEditCommand = new RelayCommand(BeginEdit);
        CancelEditCommand = new RelayCommand(CancelEdit);
        SaveEditCommand = new RelayCommand(SaveEdit);
        DeleteCommand = new RelayCommand(DeleteClicked);
        CopyPublicKeyCommand = new RelayCommand(() => Host.CopyText(PublicKeyFull));
        AddPeerCommand = new RelayCommand(AddPeer);
        AddAddressCommand = new RelayCommand(AddAddress);
        RemoveAddressCommand = new RelayCommand(p => Addresses.Remove((string)p!));
        GenerateKeyCommand = new RelayCommand(GenerateKey);
        ToggleTextModeCommand = new RelayCommand(ToggleTextMode);
    }

    // The interface shows the derived public key by default; the reveal toggle (bound to
    // this) swaps in the private-key field so it can be pasted or regenerated. Reset each
    // time editing starts.
    bool _editingPrivateKey;
    public bool EditingPrivateKey { get => _editingPrivateKey; set => Set(ref _editingPrivateKey, value); }

    // Created via Ctrl+N and never saved: cancelling deletes it instead of keeping a stub.
    public bool IsDraft { get; set; }

    void LoadFromConfig()
    {
        Name = Config!.Name;
        ListenPortText = Config.ListenPort > 0 ? Config.ListenPort.ToString() : "";
        PeerViewModel.Fill(Addresses, Config.Addresses.Select(WireGuardConf.NormalizeCidr));
        Peers.Clear();
        foreach (var p in Config.Peers)
        {
            var vm = new PeerViewModel(this)
            {
                Name = p.Name ?? "",
                PublicKey = p.PublicKey,
                Endpoint = p.Endpoint,
                Dns = p.Dns ?? "",
                KeepaliveText = p.PersistentKeepalive > 0 ? p.PersistentKeepalive.ToString() : "",
                PingHostText = p.PingHost ?? "",
                PingPeriodText = p.PingPeriod > 0 ? p.PingPeriod.ToString() : "",
                PingTimeoutText = p.PingTimeout > 0 ? p.PingTimeout.ToString() : "",
                PingDownText = p.PingDownCount > 0 ? p.PingDownCount.ToString() : "",
                PingUpText = p.PingUpCount > 0 ? p.PingUpCount.ToString() : "",
                MetricText = p.Metric != 0 ? p.Metric.ToString() : "",
            };
            PeerViewModel.Fill(vm.AllowedIps, p.AllowedIps.Select(WireGuardConf.NormalizeCidr));
            PeerViewModel.FillDomains(vm.Domains, p.Domains);
            Peers.Add(vm);
        }
        RefreshPublicKey();
    }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _publicKeyFull = "";
    public string PublicKeyFull
    {
        get => _publicKeyFull;
        set { if (Set(ref _publicKeyFull, value)) Raise(nameof(PublicKeyShort)); }
    }

    // Abbreviated derived public key for the identity row's metadata link.
    public string PublicKeyShort =>
        PublicKeyFull.Length > 14 ? $"{PublicKeyFull[..8]}…{PublicKeyFull[^4..]}" : PublicKeyFull;

    public ObservableCollection<object> Addresses { get; } = new() { new AddSlot() };
    public IEnumerable<string> AddressValues => Addresses.OfType<string>();
    public ObservableCollection<PeerViewModel> Peers { get; } = new();

    string _newAddress = "";
    public string NewAddress
    {
        get => _newAddress;
        set { if (Set(ref _newAddress, value)) AddressAddError = false; }
    }

    bool _addressAddError;
    public bool AddressAddError { get => _addressAddError; set => Set(ref _addressAddError, value); }

    string _listenPortText = "";
    public string ListenPortText { get => _listenPortText; set => Set(ref _listenPortText, value); }

    string _connSnapshot = "";

    string _privateKeyEdit = "";
    public string PrivateKeyEdit
    {
        get => _privateKeyEdit;
        set { if (Set(ref _privateKeyEdit, value)) RefreshDerivedPublicKey(); }
    }

    bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            if (IsExternal) return; // external adapters are driven by the official client

            // The power button doubles as save-and-apply while editing: commit the edits
            // first, then connect/activate the freshly-saved config.
            if (IsEditing) SaveEdit();

            // Turning a tunnel ON validates the saved config; a bad/empty tunnel can be
            // saved, but it can't be connected — report the problem and stay off.
            if (value && !IsCustom)
            {
                var error = ValidateConfig();
                if (error is not null)
                {
                    ValidationError = error;
                    Raise(); // revert the toggle visual
                    Host.ReportError(this, error);
                    return;
                }
            }

            // Optimistic UI: the host reverts via SetConnectedState on failure.
            _isConnected = value;
            if (!value) ResetEstablishment();
            Raise();
            Raise(nameof(StatsVisible));
            Raise(nameof(ConnLabel));
            RaiseDotState();
            if (IsCustom)
            {
                Custom!.Active = value;
                Host.CustomActiveChanged(this);
                return;
            }
            // Record the user's on/off intent so it can be restored at next startup. Written
            // only here (the explicit toggle: power button or tray) — never in SetConnectedState,
            // so a transient connect failure or app exit doesn't clear it.
            Config!.Connected = value;
            Host.PersistConnectedState(this);
            if (value) Host.RequestConnect(this);
            else Host.RequestDisconnect(this);
        }
    }

    public void SetConnectedState(bool connected)
    {
        if (_isConnected == connected) return;
        _isConnected = connected;
        if (!connected) ResetEstablishment();
        Raise(nameof(IsConnected));
        Raise(nameof(StatsVisible));
        Raise(nameof(ConnLabel));
        RaiseDotState();
    }

    // "Connected" means a WireGuard handshake actually completed — adapter-up alone only
    // counts as connecting. Externals/custom have no handshake, so up == established.
    bool _isEstablished;
    bool _everEstablished;
    public bool IsEstablished
    {
        get => IsExternal || IsCustom ? IsConnected : _isEstablished;
        private set
        {
            if (_isEstablished == value) return;
            _isEstablished = value;
            if (value) _everEstablished = true;
            Raise(nameof(ConnLabel));
            RaiseDotState();
        }
    }

    // Demo-harness seeding only: fake tunnels come up "established" so the connecting
    // pulse (ShowConnecting) doesn't run forever on cards that never handshake.
    public void MarkEstablished() => IsEstablished = true;

    void ResetEstablishment()
    {
        _isEstablished = false;
        _everEstablished = false;
        RateHistory.Clear(); // the sparkline restarts with the next connection
        // Live readouts are meaningless once the tunnel is down (or reconnecting with
        // fresh counters); clear them so the collapsed view never shows a frozen
        // handshake/RTT/totals as if it were current.
        foreach (var p in Peers)
        {
            p.HasStats = false;
            p.HandshakeText = "";
            p.PingText = "";
            p.TxTotalText = "";
            p.RxTotalText = "";
            p.FailoverRole = "";
            p.FirstHandshake = null;
            p.UptimeText = "";
        }
        StatsTick++; // rebuild the collapsed detail without the stale status line
    }

    // A connection-affecting save reconnects the tunnel: show "Connecting…" again rather
    // than "Stalled" while the fresh adapter completes its first handshake.
    public void MarkReconnecting()
    {
        ResetEstablishment();
        Raise(nameof(ConnLabel));
        RaiseDotState();
    }

    void RaiseDotState()
    {
        Raise(nameof(IsEstablished));
        Raise(nameof(ShowUp));
        Raise(nameof(ShowConnecting));
    }

    public bool ShowUp => IsConnected && IsEstablished;
    public bool ShowConnecting => IsConnected && !IsEstablished;

    public bool StatsVisible => IsConnected && !IsExternal && !IsCustom;
    public bool ShowConnect => !IsExternal; // WG tunnels connect; custom card activates
    public bool NameEditable => IsEditing && !IsExternal && !IsCustom;
    public string ConnLabel => !IsConnected ? "Disconnected"
        : IsEstablished ? "Connected"
        : _everEstablished ? "Stalled — no recent handshake" : "Connecting…";

    // Expanded IS edit mode: clicking the card opens editing; clicking blank card
    // space again, or Cancel/Save, collapses it. No read-only expanded state.
    bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (!Set(ref _isEditing, value)) return;
            Raise(nameof(NameEditable));
            foreach (var p in Peers) p.IsEditing = value;
        }
    }

    // Collapsed: our address(es) sit right next to the tunnel name, no label.
    public string CollapsedSummary => string.Join(", ", AddressValues);

    // Raised whenever collapsed-view content changes; the view rebuilds its detail row.
    public void NotifyPresentation() => Raise(nameof(CollapsedSummary));

    public bool ShowInterfaceSection => !IsExternal && !IsCustom;
    public string AddPeerLabel => IsCustom ? "+ Add DNS rule" : "+ Add peer";

    string _warningText = "";
    public string WarningText { get => _warningText; set => Set(ref _warningText, value); }

    string _validationError = "";
    public string ValidationError { get => _validationError; set => Set(ref _validationError, value); }

    // Two-click delete: first click arms (bold "Delete?"), second within 3 s deletes.
    bool _deleteArmed;
    public bool DeleteArmed
    {
        get => _deleteArmed;
        set { if (Set(ref _deleteArmed, value)) Raise(nameof(DeleteLabel)); }
    }

    public string DeleteLabel => DeleteArmed ? "Delete tunnel?" : "Delete tunnel";

    int _armVersion;

    // The view sets this to play a fade-out before the tunnel is actually removed. It must
    // invoke the supplied completion when the animation finishes; returns true if it will.
    public Func<Action, bool>? RemovalAnimator;

    async void DeleteClicked()
    {
        if (DeleteArmed)
        {
            DeleteArmed = false;
            if (RemovalAnimator is not null && RemovalAnimator(() => Host.RequestDelete(this))) return;
            Host.RequestDelete(this);
            return;
        }
        DeleteArmed = true;
        var version = ++_armVersion;
        await Task.Delay(3000);
        if (version == _armVersion) DeleteArmed = false;
    }

    string _upRate = "";
    public string UpRate { get => _upRate; set => Set(ref _upRate, value); }

    string _downRate = "";
    public string DownRate { get => _downRate; set => Set(ref _downRate, value); }

    public RelayCommand BeginEditCommand { get; private set; } = null!;
    public RelayCommand CancelEditCommand { get; private set; } = null!;
    public RelayCommand SaveEditCommand { get; private set; } = null!;
    public RelayCommand DeleteCommand { get; private set; } = null!;
    public RelayCommand CopyPublicKeyCommand { get; private set; } = null!;
    public RelayCommand AddPeerCommand { get; private set; } = null!;
    public RelayCommand AddAddressCommand { get; private set; } = null!;
    public RelayCommand RemoveAddressCommand { get; private set; } = null!;
    public RelayCommand GenerateKeyCommand { get; private set; } = null!;
    public RelayCommand ToggleTextModeCommand { get; private set; } = null!;

    // Raw-config editing (Ctrl+E): the whole staged tunnel as wg-quick text,
    // with per-peer DNS/Domains as SplitGuard extension keys.
    bool _isTextMode;
    public bool IsTextMode { get => _isTextMode; set => Set(ref _isTextMode, value); }

    string _configText = "";
    public string ConfigText { get => _configText; set => Set(ref _configText, value); }

    void ToggleTextMode()
    {
        if (IsExternal || IsCustom || !IsEditing) return;
        if (!IsTextMode)
        {
            ConfigText = SerializeStaged();
            IsTextMode = true;
        }
        else
        {
            ApplyTextToFields();
            IsTextMode = false;
        }
    }

    string SerializeStaged()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {PrivateKeyEdit.Trim()}");
        if (ListenPortText.Trim().Length > 0) sb.AppendLine($"ListenPort = {ListenPortText.Trim()}");
        if (AddressValues.Any()) sb.AppendLine($"Address = {string.Join(", ", AddressValues)}");
        foreach (var p in Peers)
        {
            sb.AppendLine();
            sb.AppendLine("[Peer]");
            if (p.Name.Trim().Length > 0) sb.AppendLine($"Name = {p.Name.Trim()}");
            sb.AppendLine($"PublicKey = {p.PublicKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(p.PresharedKey)) sb.AppendLine($"PresharedKey = {p.PresharedKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(p.Endpoint)) sb.AppendLine($"Endpoint = {p.Endpoint.Trim()}");
            if (p.AllowedIpValues.Any()) sb.AppendLine($"AllowedIPs = {string.Join(", ", p.AllowedIpValues)}");
            if (p.ParsedKeepalive > 0) sb.AppendLine($"PersistentKeepalive = {p.ParsedKeepalive}");
            if (p.HasDns) sb.AppendLine($"DNS = {p.Dns.Trim()}");
            if (p.DomainValues.Any()) sb.AppendLine($"Domains = {string.Join(", ", p.DomainValues)}");
            if (p.PingHostText.Trim().Length > 0) sb.AppendLine($"PingHost = {p.PingHostText.Trim()}");
            if (p.ParsedPingPeriod > 0) sb.AppendLine($"PingPeriod = {p.ParsedPingPeriod}");
            if (p.ParsedPingTimeout > 0) sb.AppendLine($"PingTimeout = {p.ParsedPingTimeout}");
            if (p.ParsedPingDown > 0) sb.AppendLine($"PingDownCount = {p.ParsedPingDown}");
            if (p.ParsedPingUp > 0) sb.AppendLine($"PingUpCount = {p.ParsedPingUp}");
            if (p.ParsedMetric != 0) sb.AppendLine($"Metric = {p.ParsedMetric}");
        }
        return sb.ToString();
    }

    void ApplyTextToFields()
    {
        var parsed = WireGuardConf.Parse(ConfigText);
        PrivateKeyEdit = parsed.PrivateKey;
        ListenPortText = parsed.ListenPort > 0 ? parsed.ListenPort.ToString() : "";
        PeerViewModel.Fill(Addresses, parsed.Addresses);
        Peers.Clear();
        foreach (var p in parsed.Peers)
        {
            var vm = new PeerViewModel(this)
            {
                Name = p.Name ?? "",
                PublicKey = p.PublicKey,
                PresharedKey = p.PresharedKey ?? "",
                Endpoint = p.Endpoint,
                Dns = p.Dns ?? "",
                KeepaliveText = p.PersistentKeepalive > 0 ? p.PersistentKeepalive.ToString() : "",
                PingHostText = p.PingHost ?? "",
                PingPeriodText = p.PingPeriod > 0 ? p.PingPeriod.ToString() : "",
                PingTimeoutText = p.PingTimeout > 0 ? p.PingTimeout.ToString() : "",
                PingDownText = p.PingDownCount > 0 ? p.PingDownCount.ToString() : "",
                PingUpText = p.PingUpCount > 0 ? p.PingUpCount.ToString() : "",
                MetricText = p.Metric != 0 ? p.Metric.ToString() : "",
                IsEditing = true,
            };
            PeerViewModel.Fill(vm.AllowedIps, p.AllowedIps);
            PeerViewModel.FillDomains(vm.Domains, p.Domains);
            Peers.Add(vm);
        }
        // A tunnel with no peers can't be represented; keep one empty peer to edit.
        if (Peers.Count == 0) Peers.Add(new PeerViewModel(this) { IsEditing = true });
        // Interface-level DNS attaches to the peer containing it, mirroring import.
        if (parsed.InterfaceDns is not null)
        {
            var target = WireGuardConf.PeerForDns(parsed);
            var idx = target is null ? 0 : parsed.Peers.IndexOf(target);
            if (idx >= 0 && idx < Peers.Count && !Peers[idx].HasDns) Peers[idx].Dns = parsed.InterfaceDns;
        }
    }

    void BeginEdit()
    {
        ValidationError = "";
        EditingPrivateKey = false; // start showing the derived public key
        if (!IsExternal && !IsCustom)
        {
            try { PrivateKeyEdit = RuleStore.Unprotect(Config!.PrivateKeyProtected); }
            catch { PrivateKeyEdit = ""; }
            foreach (var (vm, cfg) in Peers.Zip(Config!.Peers))
            {
                if (cfg.PresharedKeyProtected is not null)
                {
                    try { vm.PresharedKey = RuleStore.Unprotect(cfg.PresharedKeyProtected); } catch { }
                }
            }
        }
        _connSnapshot = ConnSnapshot();
        Host.EditStarted(this); // collapses any other expanded card
        // The card always opens with WG peers collapsed (DNS-only rows have no header
        // worth collapsing to, so they stay open).
        foreach (var p in Peers) p.IsExpanded = p.IsDnsOnly;
        IsEditing = true;
    }

    // Everything that requires a tunnel reconnect when it changes (DNS/domains excluded).
    string ConnSnapshot() => string.Join("|",
        Name.Trim(), PrivateKeyEdit.Trim(), ListenPortText.Trim(),
        string.Join(",", AddressValues),
        string.Join(";", Peers.Select(p =>
            $"{p.PublicKey.Trim()},{p.PresharedKey.Trim()},{p.Endpoint.Trim()},{string.Join("+", p.AllowedIpValues)},{p.ParsedKeepalive},{p.PingHostText.Trim()},{p.ParsedPingPeriod},{p.ParsedPingTimeout},{p.ParsedPingDown},{p.ParsedPingUp},{p.ParsedMetric}")));

    void CancelEdit()
    {
        DeleteArmed = false;
        IsTextMode = false;
        if (IsDraft)
        {
            IsEditing = false;
            Host.RequestDelete(this);
            return;
        }
        IsEditing = false;
        PrivateKeyEdit = "";
        ValidationError = "";
        if (IsExternal)
        {
            Peers[0].Dns = External!.Dns ?? "";
            PeerViewModel.FillDomains(Peers[0].Domains, External.Domains);
        }
        else if (IsCustom)
        {
            LoadFromCustom();
        }
        else
        {
            LoadFromConfig();
        }
    }

    void LoadFromCustom()
    {
        Peers.Clear();
        if (Custom!.Roles.Count == 0) Custom.Roles.Add(new CustomDnsRole());
        foreach (var role in Custom.Roles)
        {
            var peer = new PeerViewModel(this) { PublicKey = role.Id, Dns = role.Dns ?? "" };
            PeerViewModel.FillDomains(peer.Domains, role.Domains);
            Peers.Add(peer);
        }
    }

    void SaveEdit()
    {
        if (IsTextMode)
        {
            ApplyTextToFields();
            IsTextMode = false;
        }
        // A tunnel can be saved incomplete/empty; problems are only enforced when it is
        // turned on (see ValidateConfig in the IsConnected setter).
        ValidationError = "";
        WarningText = string.Join(" · ", Peers.SelectMany(p => new[] { p.DnsRouteWarning(), p.PingRouteWarning() }).Where(w => w is not null)!);
        var connectionChanged = !IsExternal && !IsCustom && ConnSnapshot() != _connSnapshot;
        if (IsCustom)
        {
            // Rebuild the role list from the DNS-only peers, keeping/creating a stable id each.
            Custom!.Roles = Peers.Select(p =>
            {
                var id = string.IsNullOrEmpty(p.PublicKey) ? Guid.NewGuid().ToString("N") : p.PublicKey;
                p.PublicKey = id;
                return new CustomDnsRole
                {
                    Id = id,
                    Dns = p.HasDns ? p.Dns.Trim() : null,
                    Domains = p.DomainValues.ToList(),
                };
            }).ToList();
        }
        else if (IsExternal)
        {
            External!.Dns = Peers[0].HasDns ? Peers[0].Dns.Trim() : null;
            External.Domains = Peers[0].DomainValues.ToList();
        }
        else
        {
            Config!.Name = Name.Trim();
            Config.PrivateKeyProtected = RuleStore.Protect(PrivateKeyEdit.Trim());
            Config.ListenPort = ushort.TryParse(ListenPortText.Trim(), out var lp) ? lp : (ushort)0;
            Config.Addresses = AddressValues.ToList();
            Config.Peers = Peers.Select(p => new PeerConfig
            {
                Name = p.Name.Trim().Length > 0 ? p.Name.Trim() : null,
                PublicKey = p.PublicKey.Trim(),
                PresharedKeyProtected = string.IsNullOrWhiteSpace(p.PresharedKey) ? null : RuleStore.Protect(p.PresharedKey.Trim()),
                Endpoint = p.Endpoint.Trim(),
                AllowedIps = p.AllowedIpValues.ToList(),
                PersistentKeepalive = p.ParsedKeepalive,
                Dns = p.HasDns ? p.Dns.Trim() : null,
                Domains = p.DomainValues.ToList(),
                PingHost = p.PingHostText.Trim().Length > 0 ? p.PingHostText.Trim() : null,
                PingTimeout = Math.Clamp(p.ParsedPingTimeout, 0, 60),
                PingPeriod = Math.Clamp(p.ParsedPingPeriod, 0, 3600),
                PingDownCount = Math.Clamp(p.ParsedPingDown, 0, 100),
                PingUpCount = Math.Clamp(p.ParsedPingUp, 0, 100),
                Metric = Math.Clamp(p.ParsedMetric, 0, 10),
            }).ToList();
        }
        IsDraft = false;
        IsEditing = false;
        PrivateKeyEdit = "";
        foreach (var p in Peers) p.PresharedKey = "";
        NotifyPresentation();
        Host.TunnelSaved(this, connectionChanged);
    }

    // Connect-time validation of the saved config (fields are cleared after save, so this
    // checks Config, not the edit boxes). Returns the first problem, or null if connectable.
    string? ValidateConfig()
    {
        if (IsExternal || IsCustom) return null;
        var c = Config!;
        if (string.IsNullOrWhiteSpace(c.Name)) return "Tunnel needs a name";
        string priv;
        try { priv = RuleStore.Unprotect(c.PrivateKeyProtected); } catch { priv = ""; }
        if (!PeerViewModel.IsValidKey(priv)) return "Private key must be a valid 32-byte base64 key";
        if (c.Addresses.Count == 0) return "Tunnel needs at least one address";
        foreach (var a in c.Addresses)
            if (!WireGuardConf.TryParseCidr(a, out _, out _)) return $"Invalid address: {a}";
        if (c.Peers.Count == 0) return "Tunnel needs at least one peer";
        foreach (var p in c.Peers)
        {
            if (!PeerViewModel.IsValidKey(p.PublicKey)) return "Peer public key must be a valid 32-byte base64 key";
            if (!PeerViewModel.IsValidEndpoint(p.Endpoint)) return $"Peer endpoint must be host:port — got '{p.Endpoint}'";
            if (p.AllowedIps.Count == 0) return "Peer needs at least one allowed IP";
            foreach (var cidr in p.AllowedIps)
                if (!WireGuardConf.TryParseCidr(cidr, out _, out _)) return $"Invalid allowed IP: {cidr}";
            if (!string.IsNullOrEmpty(p.PingHost) && !System.Net.IPAddress.TryParse(p.PingHost, out _))
                return $"Ping host must be an IP address — got '{p.PingHost}'";
        }
        // Overlapping allowed IPs with equal metrics can't be arbitrated.
        if (Host.MetricConflict(this) is { } conflict) return conflict;
        return null;
    }

    // A freshly added peer opens expanded so it can be filled in immediately.
    void AddPeer() => Peers.Add(new PeerViewModel(this) { IsEditing = true, IsExpanded = true });

    public void RemovePeer(PeerViewModel peer)
    {
        if (Peers.Count > 1) Peers.Remove(peer);
    }

    void AddAddress()
    {
        var cidr = WireGuardConf.NormalizeCidr(NewAddress);
        if (!WireGuardConf.TryParseCidr(cidr, out _, out _))
        {
            AddressAddError = true;
            return;
        }
        Addresses.Insert(Addresses.Count - 1, cidr);
        NewAddress = "";
    }

    // Regenerate a fresh keypair. Reachable only from the revealed private-key field, so
    // replacing an existing key is the explicit intent.
    void GenerateKey() => PrivateKeyEdit = Convert.ToBase64String(Curve25519.GeneratePrivateKey());

    void RefreshDerivedPublicKey()
    {
        if (PeerViewModel.IsValidKey(PrivateKeyEdit))
            PublicKeyFull = Convert.ToBase64String(Curve25519.GetPublicKey(Convert.FromBase64String(PrivateKeyEdit.Trim())));
    }

    public void RefreshPublicKey()
    {
        if (IsExternal) return;
        try
        {
            var priv = Convert.FromBase64String(RuleStore.Unprotect(Config!.PrivateKeyProtected));
            PublicKeyFull = Convert.ToBase64String(Curve25519.GetPublicKey(priv));
        }
        catch
        {
            PublicKeyFull = "(key unavailable)";
        }
    }

    static readonly TimeSpan HandshakeFresh = TimeSpan.FromSeconds(180);

    public void ApplyStats(TunnelStats stats)
    {
        double up = 0, down = 0;
        DateTime? newest = null;
        foreach (var peer in Peers)
        {
            if (!stats.PerPeer.TryGetValue(peer.PublicKey, out var s)) continue;
            up += s.UpBps;
            down += s.DownBps;
            peer.HasStats = true;
            peer.TxTotalText = Format.Bytes(s.TotalTx);
            peer.RxTotalText = Format.Bytes(s.TotalRx);
            if (s.Handshake is not null)
            {
                peer.FirstHandshake ??= s.Handshake;
                peer.UptimeText = Format.Duration(DateTime.UtcNow - peer.FirstHandshake.Value);
            }
            if (s.Handshake is not null && (newest is null || s.Handshake > newest)) newest = s.Handshake;
            peer.HandshakeText = Format.Ago(s.Handshake);
            peer.PingText = s.PingOk switch
            {
                true => $"{s.PingMs:0} ms",
                false => "no reply",
                _ => "",
            };
            peer.FailoverRole = s.FailoverRole ?? "";
        }
        UpRate = Format.Rate(up);
        DownRate = Format.Rate(down);
        PushRateSample(up + down);
        if (IsConnected)
            IsEstablished = newest is not null && DateTime.UtcNow - newest < HandshakeFresh;
        // Refresh the collapsed-card detail so its per-peer handshake/RTT stay live.
        StatsTick++;
    }

    // Bumped every stats poll; the card watches it to rebuild the collapsed detail.
    int _statsTick;
    public int StatsTick { get => _statsTick; set => Set(ref _statsTick, value); }

    // Rolling total-throughput samples (up+down Bps, one per stats poll) for the header
    // sparkline; the card redraws it on StatsTick.
    public const int SparkCapacity = 40;
    public List<double> RateHistory { get; } = new();

    public void PushRateSample(double bps)
    {
        RateHistory.Add(bps);
        if (RateHistory.Count > SparkCapacity) RateHistory.RemoveAt(0);
    }
}
