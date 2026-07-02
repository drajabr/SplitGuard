using System.Collections.ObjectModel;
using WgSplitDns.Models;
using WgSplitDns.Services;

namespace WgSplitDns.ViewModels;

public interface ITunnelHost
{
    void RequestConnect(TunnelViewModel tunnel);
    void RequestDisconnect(TunnelViewModel tunnel);
    void TogglePin(TunnelViewModel tunnel, PeerViewModel peer);
    void DomainAdded(TunnelViewModel tunnel, PeerViewModel peer, string domain);
    void DomainRemoved(TunnelViewModel tunnel, PeerViewModel peer, string domain);
    bool IsDomainInUse(string domain, PeerViewModel except);
    void TunnelSaved(TunnelViewModel tunnel);
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
        foreach (var d in external.Domains) peer.Domains.Add(d);
        Peers.Add(peer);
    }

    void InitCommands()
    {
        BeginEditCommand = new RelayCommand(BeginEdit);
        CancelEditCommand = new RelayCommand(CancelEdit);
        SaveEditCommand = new RelayCommand(SaveEdit);
        DeleteCommand = new RelayCommand(() => Host.RequestDelete(this));
        CopyPublicKeyCommand = new RelayCommand(() => Host.CopyText(PublicKeyFull));
        AddPeerCommand = new RelayCommand(AddPeer);
        AddAddressCommand = new RelayCommand(AddAddress);
        RemoveAddressCommand = new RelayCommand(p => Addresses.Remove((string)p!));
        GenerateKeyCommand = new RelayCommand(GenerateKey);
    }

    void LoadFromConfig()
    {
        Name = Config!.Name;
        Addresses.Clear();
        foreach (var a in Config.Addresses) Addresses.Add(a);
        Peers.Clear();
        foreach (var p in Config.Peers)
        {
            var vm = new PeerViewModel(this)
            {
                PublicKey = p.PublicKey,
                Endpoint = p.Endpoint,
                Dns = p.Dns ?? "",
                HasPsk = p.PresharedKeyProtected is not null,
                PersistentKeepalive = p.PersistentKeepalive,
            };
            foreach (var ip in p.AllowedIps) vm.AllowedIps.Add(ip);
            foreach (var d in p.Domains) vm.Domains.Add(d);
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
        PublicKeyFull.Length > 20 ? $"{PublicKeyFull[..14]}…{PublicKeyFull[^7..]}" : PublicKeyFull;

    public ObservableCollection<string> Addresses { get; } = new();
    public ObservableCollection<PeerViewModel> Peers { get; } = new();

    string _newAddress = "";
    public string NewAddress
    {
        get => _newAddress;
        set { if (Set(ref _newAddress, value)) AddressAddError = false; }
    }

    bool _addressAddError;
    public bool AddressAddError { get => _addressAddError; set => Set(ref _addressAddError, value); }

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

    public bool ShowToggle => !IsEditing && !IsExternal;
    public bool ShowInterfaceSection => !IsExternal;

    string _statusError = "";
    public string StatusError { get => _statusError; set => Set(ref _statusError, value); }

    string _warningText = "";
    public string WarningText { get => _warningText; set => Set(ref _warningText, value); }

    string _validationError = "";
    public string ValidationError { get => _validationError; set => Set(ref _validationError, value); }

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
        IsEditing = true;
    }

    void CancelEdit()
    {
        IsEditing = false;
        PrivateKeyEdit = "";
        ValidationError = "";
        if (IsExternal)
        {
            Peers[0].Dns = External!.Dns ?? "";
            Peers[0].Domains.Clear();
            foreach (var d in External.Domains) Peers[0].Domains.Add(d);
        }
        else
        {
            LoadFromConfig();
        }
    }

    void SaveEdit()
    {
        var error = Validate();
        if (error is not null)
        {
            ValidationError = error;
            return;
        }
        ValidationError = "";
        WarningText = string.Join(" · ", Peers.Select(p => p.DnsRouteWarning()).Where(w => w is not null)!);
        if (IsExternal)
        {
            External!.Dns = Peers[0].HasDns ? Peers[0].Dns.Trim() : null;
            External.Domains = Peers[0].Domains.ToList();
        }
        else
        {
            Config!.Name = Name.Trim();
            Config.PrivateKeyProtected = RuleStore.Protect(PrivateKeyEdit.Trim());
            Config.Addresses = Addresses.ToList();
            Config.Peers = Peers.Select(p => new PeerConfig
            {
                PublicKey = p.PublicKey.Trim(),
                PresharedKeyProtected = string.IsNullOrWhiteSpace(p.PresharedKey) ? null : RuleStore.Protect(p.PresharedKey.Trim()),
                Endpoint = p.Endpoint.Trim(),
                AllowedIps = p.AllowedIps.ToList(),
                PersistentKeepalive = p.PersistentKeepalive,
                Dns = p.HasDns ? p.Dns.Trim() : null,
                Domains = p.Domains.ToList(),
            }).ToList();
            foreach (var p in Peers) p.HasPsk = !string.IsNullOrWhiteSpace(p.PresharedKey);
        }
        IsEditing = false;
        PrivateKeyEdit = "";
        foreach (var p in Peers) p.PresharedKey = "";
        Host.TunnelSaved(this);
    }

    string? Validate()
    {
        if (!IsExternal)
        {
            if (string.IsNullOrWhiteSpace(Name)) return "Tunnel needs a name";
            if (!PeerViewModel.IsValidKey(PrivateKeyEdit)) return "Private key must be a valid 32-byte base64 key";
            if (Addresses.Count == 0) return "Tunnel needs at least one address";
            foreach (var a in Addresses)
                if (!WireGuardConf.TryParseCidr(a, out _, out _)) return $"Invalid address: {a}";
            if (Peers.Count == 0) return "Tunnel needs at least one peer";
        }
        foreach (var p in Peers)
        {
            var err = p.Validate();
            if (err is not null) return err;
        }
        var all = Peers.SelectMany(p => p.Domains).ToList();
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
        Addresses.Add(cidr);
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
