using System.Collections.ObjectModel;
using SplitGuard.Models;
using SplitGuard.Services;

namespace SplitGuard.ViewModels;

public interface ITunnelHost
{
    void RequestConnect(TunnelViewModel tunnel);
    void RequestDisconnect(TunnelViewModel tunnel);
    void TogglePin(TunnelViewModel tunnel, PeerViewModel peer);
    bool IsDomainInUse(string domain, PeerViewModel except);
    void EditStarted(TunnelViewModel tunnel);
    void TunnelSaved(TunnelViewModel tunnel, bool connectionChanged);
    void RequestDelete(TunnelViewModel tunnel);
    void CopyText(string text);
    void AccentChanged(TunnelViewModel tunnel);
    void CustomActiveChanged(TunnelViewModel tunnel);
    void ReportError(TunnelViewModel tunnel, string message);
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
        PeerViewModel.Fill(peer.Domains, external.Domains);
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
            PeerViewModel.Fill(peer.Domains, role.Domains);
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
        ClearListenPortCommand = new RelayCommand(() => ListenPortText = "");
        ToggleTextModeCommand = new RelayCommand(ToggleTextMode);
    }

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
                PublicKey = p.PublicKey,
                Endpoint = p.Endpoint,
                Dns = p.Dns ?? "",
                KeepaliveText = p.PersistentKeepalive > 0 ? p.PersistentKeepalive.ToString() : "",
                PingHostText = p.PingHost ?? "",
                PriorityText = p.Priority != 0 ? p.Priority.ToString() : "",
            };
            PeerViewModel.Fill(vm.AllowedIps, p.AllowedIps.Select(WireGuardConf.NormalizeCidr));
            PeerViewModel.Fill(vm.Domains, p.Domains);
            Peers.Add(vm);
        }
        RefreshPublicKey();
    }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _publicKeyFull = "";
    public string PublicKeyFull { get => _publicKeyFull; set => Set(ref _publicKeyFull, value); }

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

    void ResetEstablishment()
    {
        _isEstablished = false;
        _everEstablished = false;
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

    // Collapsed: show our own listen port (peer endpoints are ambiguous with many peers).
    public string CollapsedSummary =>
        ListenPortText.Trim().Length > 0 ? $"listen :{ListenPortText.Trim()}" : "";

    // Collapsed detail tokens (built + syntax-colored in the view).
    public IEnumerable<string> AllDomains => Peers.SelectMany(p => p.DomainValues).Distinct();

    public string CollapsedAllowedIps
    {
        get
        {
            var ips = Peers.SelectMany(p => p.AllowedIpValues).Distinct().ToList();
            return ips.Count == 0 ? "" : string.Join(", ", ips.Take(3)) + (ips.Count > 3 ? $" +{ips.Count - 3}" : "");
        }
    }

    // Raised whenever collapsed-view content changes; the view rebuilds its detail row.
    public void NotifyPresentation()
    {
        Raise(nameof(CollapsedSummary));
        Raise(nameof(CollapsedAllowedIps));
    }

    public bool ShowInterfaceSection => !IsExternal && !IsCustom;
    public string AddPeerLabel => IsCustom ? "+ Add DNS rule" : "+ Add peer";

    // Optional per-card accent hue (null = follow the global accent). Cycled by clicking
    // the header dot while editing; the view resolves the name to a color and overrides
    // AccentBrush inside this card only.
    public string? Accent
    {
        get => IsCustom ? Custom!.Accent : IsExternal ? External!.Accent : Config!.Accent;
        set
        {
            if (IsCustom) Custom!.Accent = value;
            else if (IsExternal) External!.Accent = value;
            else Config!.Accent = value;
            Raise();
        }
    }

    public void CycleAccent()
    {
        Accent = Accents.Next(Accent);
        Host.AccentChanged(this);
    }


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

    public string DeleteLabel => DeleteArmed ? "Delete?" : "Delete";

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

    string _headerHandshake = "";
    public string HeaderHandshake { get => _headerHandshake; set => Set(ref _headerHandshake, value); }

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
    public RelayCommand ClearListenPortCommand { get; private set; } = null!;
    public RelayCommand ToggleTextModeCommand { get; private set; } = null!;

    // Two-click confirm for generating a new keypair (it discards the current one).
    bool _generateArmed;
    public bool GenerateArmed
    {
        get => _generateArmed;
        set { if (Set(ref _generateArmed, value)) Raise(nameof(GenerateLabel)); }
    }
    public string GenerateLabel => GenerateArmed ? "Confirm?" : "Generate";
    int _genArmVersion;

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
            sb.AppendLine($"PublicKey = {p.PublicKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(p.PresharedKey)) sb.AppendLine($"PresharedKey = {p.PresharedKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(p.Endpoint)) sb.AppendLine($"Endpoint = {p.Endpoint.Trim()}");
            if (p.AllowedIpValues.Any()) sb.AppendLine($"AllowedIPs = {string.Join(", ", p.AllowedIpValues)}");
            if (p.ParsedKeepalive > 0) sb.AppendLine($"PersistentKeepalive = {p.ParsedKeepalive}");
            if (p.HasDns) sb.AppendLine($"DNS = {p.Dns.Trim()}");
            if (p.DomainValues.Any()) sb.AppendLine($"Domains = {string.Join(", ", p.DomainValues)}");
            if (p.PingHostText.Trim().Length > 0) sb.AppendLine($"PingHost = {p.PingHostText.Trim()}");
            if (p.ParsedPriority != 0) sb.AppendLine($"Priority = {p.ParsedPriority}");
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
                PublicKey = p.PublicKey,
                PresharedKey = p.PresharedKey ?? "",
                Endpoint = p.Endpoint,
                Dns = p.Dns ?? "",
                KeepaliveText = p.PersistentKeepalive > 0 ? p.PersistentKeepalive.ToString() : "",
                PingHostText = p.PingHost ?? "",
                PriorityText = p.Priority != 0 ? p.Priority.ToString() : "",
                IsEditing = true,
            };
            PeerViewModel.Fill(vm.AllowedIps, p.AllowedIps);
            PeerViewModel.Fill(vm.Domains, p.Domains);
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
        IsEditing = true;
    }

    // Everything that requires a tunnel reconnect when it changes (DNS/domains excluded).
    string ConnSnapshot() => string.Join("|",
        Name.Trim(), PrivateKeyEdit.Trim(), ListenPortText.Trim(),
        string.Join(",", AddressValues),
        string.Join(";", Peers.Select(p =>
            $"{p.PublicKey.Trim()},{p.PresharedKey.Trim()},{p.Endpoint.Trim()},{string.Join("+", p.AllowedIpValues)},{p.ParsedKeepalive},{p.PingHostText.Trim()},{p.ParsedPriority}")));

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
            PeerViewModel.Fill(Peers[0].Domains, External.Domains);
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
            PeerViewModel.Fill(peer.Domains, role.Domains);
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
                PublicKey = p.PublicKey.Trim(),
                PresharedKeyProtected = string.IsNullOrWhiteSpace(p.PresharedKey) ? null : RuleStore.Protect(p.PresharedKey.Trim()),
                Endpoint = p.Endpoint.Trim(),
                AllowedIps = p.AllowedIpValues.ToList(),
                PersistentKeepalive = p.ParsedKeepalive,
                Dns = p.HasDns ? p.Dns.Trim() : null,
                Domains = p.DomainValues.ToList(),
                PingHost = p.PingHostText.Trim().Length > 0 ? p.PingHostText.Trim() : null,
                Priority = p.ParsedPriority,
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
        return null;
    }

    void AddPeer() => Peers.Add(new PeerViewModel(this) { IsEditing = true });

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

    async void GenerateKey()
    {
        if (GenerateArmed)
        {
            GenerateArmed = false;
            PrivateKeyEdit = Convert.ToBase64String(Curve25519.GeneratePrivateKey());
            return;
        }
        GenerateArmed = true;
        var version = ++_genArmVersion;
        await Task.Delay(3000);
        if (version == _genArmVersion) GenerateArmed = false;
    }

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
            if (s.Handshake is not null && (newest is null || s.Handshake > newest)) newest = s.Handshake;
            peer.HandshakeText = Format.Ago(s.Handshake);
            peer.ShowHandshake = Peers.Count > 1;
            peer.PingText = s.PingOk switch
            {
                true => $"ping {s.PingMs:0} ms",
                false => "ping failed",
                _ => "",
            };
            peer.FailoverRole = s.FailoverRole ?? "";
        }
        UpRate = Format.Rate(up);
        DownRate = Format.Rate(down);
        HeaderHandshake = Peers.Count == 1 ? Format.Ago(newest) : "";
        if (IsConnected)
            IsEstablished = newest is not null && DateTime.UtcNow - newest < HandshakeFresh;
    }
}
