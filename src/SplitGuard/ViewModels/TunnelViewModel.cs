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
}

public class TunnelViewModel : ObservableObject
{
    public ITunnelHost Host { get; }
    public TunnelConfig? Config { get; }
    public ExternalRuleConfig? External { get; }

    public bool IsExternal => External is not null;

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

    // Created via Ctrl+N and never saved: cancelling deletes it instead of keeping a stub.
    public bool IsDraft { get; set; }

    void LoadFromConfig()
    {
        Name = Config!.Name;
        ListenPortText = Config.ListenPort > 0 ? Config.ListenPort.ToString() : "";
        PeerViewModel.Fill(Addresses, Config.Addresses);
        Peers.Clear();
        foreach (var p in Config.Peers)
        {
            var vm = new PeerViewModel(this)
            {
                PublicKey = p.PublicKey,
                Endpoint = p.Endpoint,
                Dns = p.Dns ?? "",
                HasPsk = p.PresharedKeyProtected is not null,
                KeepaliveText = p.PersistentKeepalive > 0 ? p.PersistentKeepalive.ToString() : "",
            };
            PeerViewModel.Fill(vm.AllowedIps, p.AllowedIps);
            PeerViewModel.Fill(vm.Domains, p.Domains);
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

    public string PublicKeyShort =>
        PublicKeyFull.Length > 12 ? $"{PublicKeyFull[..5]}…{PublicKeyFull[^5..]}" : PublicKeyFull;

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
    public string ListenPortText
    {
        get => _listenPortText;
        set { if (Set(ref _listenPortText, value)) Raise(nameof(ListenPortDisplay)); }
    }

    public string ListenPortDisplay => ListenPortText.Trim().Length > 0 ? $"port {ListenPortText.Trim()}" : "port auto";

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
            if (IsExternal) return; // external adapters are read-only
            // Optimistic UI: the host reverts via SetConnectedState on failure.
            _isConnected = value;
            Raise();
            Raise(nameof(StatsVisible));
            if (value) Host.RequestConnect(this);
            else Host.RequestDisconnect(this);
        }
    }

    public void SetConnectedState(bool connected)
    {
        if (_isConnected == connected) return;
        _isConnected = connected;
        Raise(nameof(IsConnected));
        Raise(nameof(StatsVisible));
    }

    public bool StatsVisible => IsConnected;

    // Expanded IS edit mode: clicking the card opens editing; clicking blank card
    // space again, or Cancel/Save, collapses it. No read-only expanded state.
    bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (!Set(ref _isEditing, value)) return;
            foreach (var p in Peers) p.IsEditing = value;
            Raise(nameof(ShowToggle));
        }
    }

    public string CollapsedSummary =>
        Peers.Select(p => p.Endpoint).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "";

    // Second collapsed line: addresses plus what this tunnel's split DNS does.
    public string CollapsedDetail
    {
        get
        {
            var parts = new List<string>();
            if (AddressValues.Any()) parts.Add(string.Join(", ", AddressValues));
            foreach (var p in Peers)
            {
                var domains = p.DomainValues.ToList();
                if (!p.HasDns && domains.Count == 0) continue;
                var list = domains.Count == 0
                    ? "no domains"
                    : string.Join(", ", domains.Take(4)) + (domains.Count > 4 ? $" +{domains.Count - 4}" : "");
                parts.Add(p.HasDns ? $"{p.Dns} → {list}" : list);
            }
            if (Peers.Any(p => p.IsPinned)) parts.Add("device DNS");
            var keepalive = Peers.FirstOrDefault(p => p.HasKeepalive);
            if (keepalive is not null) parts.Add(keepalive.KeepaliveDisplay);
            return parts.Count == 0 ? "no split DNS configured" : string.Join("  ·  ", parts);
        }
    }

    public string CollapsedAllowedIps
    {
        get
        {
            var ips = Peers.SelectMany(p => p.AllowedIpValues).Distinct().ToList();
            return ips.Count == 0 ? "" : string.Join(", ", ips.Take(3)) + (ips.Count > 3 ? $" +{ips.Count - 3}" : "");
        }
    }

    public void NotifyPresentation()
    {
        Raise(nameof(CollapsedSummary));
        Raise(nameof(CollapsedDetail));
        Raise(nameof(CollapsedAllowedIps));
    }

    public bool ShowToggle => !IsEditing && !IsExternal;
    public bool ShowInterfaceSection => !IsExternal;


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

    async void DeleteClicked()
    {
        if (DeleteArmed)
        {
            DeleteArmed = false;
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
    public RelayCommand ToggleTextModeCommand { get; private set; } = null!;

    // Raw-config editing (Ctrl+E): the whole staged tunnel as wg-quick text,
    // with per-peer DNS/Domains as SplitGuard extension keys.
    bool _isTextMode;
    public bool IsTextMode { get => _isTextMode; set => Set(ref _isTextMode, value); }

    string _configText = "";
    public string ConfigText { get => _configText; set => Set(ref _configText, value); }

    void ToggleTextMode()
    {
        if (IsExternal || !IsEditing) return;
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
        if (!IsExternal)
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
            $"{p.PublicKey.Trim()},{p.PresharedKey.Trim()},{p.Endpoint.Trim()},{string.Join("+", p.AllowedIpValues)},{p.ParsedKeepalive}")));

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
        else
        {
            LoadFromConfig();
        }
    }

    void SaveEdit()
    {
        if (IsTextMode)
        {
            ApplyTextToFields();
            IsTextMode = false;
        }
        var error = Validate();
        if (error is not null)
        {
            ValidationError = error;
            return;
        }
        ValidationError = "";
        WarningText = string.Join(" · ", Peers.Select(p => p.DnsRouteWarning()).Where(w => w is not null)!);
        var connectionChanged = !IsExternal && ConnSnapshot() != _connSnapshot;
        if (IsExternal)
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
            }).ToList();
            foreach (var p in Peers) p.HasPsk = !string.IsNullOrWhiteSpace(p.PresharedKey);
        }
        IsDraft = false;
        IsEditing = false;
        PrivateKeyEdit = "";
        foreach (var p in Peers) p.PresharedKey = "";
        NotifyPresentation();
        Host.TunnelSaved(this, connectionChanged);
    }

    string? Validate()
    {
        if (!IsExternal)
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Tunnel needs a name";
            if (!PeerViewModel.IsValidKey(PrivateKeyEdit)) return "Private key must be a valid 32-byte base64 key";
            if (ListenPortText.Trim().Length > 0 && !ushort.TryParse(ListenPortText.Trim(), out _))
                return $"Listen port must be 0-65535 — got '{ListenPortText}'";
            if (!AddressValues.Any()) return "Tunnel needs at least one address";
            foreach (var a in AddressValues)
                if (!WireGuardConf.TryParseCidr(a, out _, out _)) return $"Invalid address: {a}";
            if (Peers.Count == 0) return "Tunnel needs at least one peer";
        }
        foreach (var p in Peers)
        {
            var err = p.Validate();
            if (err is not null) return err;
        }
        var all = Peers.SelectMany(p => p.DomainValues).ToList();
        var dup = all.GroupBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) return $"Domain listed twice: {dup.Key}";
        return null;
    }

    void AddPeer() => Peers.Add(new PeerViewModel(this) { IsEditing = true });

    public void RemovePeer(PeerViewModel peer)
    {
        if (Peers.Count > 1) Peers.Remove(peer);
    }

    void AddAddress()
    {
        var cidr = NewAddress.Trim();
        if (!WireGuardConf.TryParseCidr(cidr, out _, out _))
        {
            AddressAddError = true;
            return;
        }
        Addresses.Insert(Addresses.Count - 1, cidr);
        NewAddress = "";
    }

    void GenerateKey()
    {
        PrivateKeyEdit = Convert.ToBase64String(Curve25519.GeneratePrivateKey());
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
        }
        UpRate = Format.Rate(up);
        DownRate = Format.Rate(down);
        HeaderHandshake = Peers.Count == 1 ? Format.Ago(newest) : "";
    }
}
