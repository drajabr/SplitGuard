using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;

namespace WgSplitDns.ViewModels;

public partial class PeerViewModel : ObservableObject
{
    readonly TunnelViewModel _tunnel;

    public PeerViewModel(TunnelViewModel tunnel)
    {
        _tunnel = tunnel;
        AddDomainCommand = new RelayCommand(AddDomain);
        RemoveDomainCommand = new RelayCommand(p => RemoveDomain((string)p!));
        AddAllowedIpCommand = new RelayCommand(AddAllowedIp);
        RemoveAllowedIpCommand = new RelayCommand(p => AllowedIps.Remove((string)p!));
        TogglePinCommand = new RelayCommand(() => _tunnel.Host.TogglePin(_tunnel, this));
        RemovePeerCommand = new RelayCommand(() => _tunnel.RemovePeer(this));
    }

    string _publicKey = "";
    public string PublicKey
    {
        get => _publicKey;
        set { if (Set(ref _publicKey, value)) { Raise(nameof(KeyShort)); } }
    }

    public string KeyShort => PublicKey.Length > 12 ? $"{PublicKey[..6]}…{PublicKey[^5..]}" : PublicKey;

    string _presharedKey = "";
    public string PresharedKey { get => _presharedKey; set => Set(ref _presharedKey, value); }

    bool _hasPsk;
    public bool HasPsk { get => _hasPsk; set => Set(ref _hasPsk, value); }

    string _endpoint = "";
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }

    string _dns = "";
    public string Dns
    {
        get => _dns;
        set { if (Set(ref _dns, value)) { Raise(nameof(HasDns)); Raise(nameof(PinEnabled)); } }
    }

    public bool HasDns => !string.IsNullOrWhiteSpace(Dns);
    public bool PinEnabled => HasDns;

    public ushort PersistentKeepalive { get; set; }

    public ObservableCollection<string> AllowedIps { get; } = new();
    public ObservableCollection<string> Domains { get; } = new();

    string _newDomain = "";
    public string NewDomain
    {
        get => _newDomain;
        set { if (Set(ref _newDomain, value)) DomainAddError = false; }
    }

    bool _domainAddError;
    public bool DomainAddError { get => _domainAddError; set => Set(ref _domainAddError, value); }

    string _newAllowedIp = "";
    public string NewAllowedIp
    {
        get => _newAllowedIp;
        set { if (Set(ref _newAllowedIp, value)) AllowedIpAddError = false; }
    }

    bool _allowedIpAddError;
    public bool AllowedIpAddError { get => _allowedIpAddError; set => Set(ref _allowedIpAddError, value); }

    bool _isEditing;
    public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }

    public bool IsExternal => _tunnel.IsExternal;

    bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (Set(ref _isPinned, value)) Raise(nameof(PinCaption)); }
    }

    bool _pinSuspended;
    public bool PinSuspended
    {
        get => _pinSuspended;
        set { if (Set(ref _pinSuspended, value)) Raise(nameof(PinCaption)); }
    }

    public string PinCaption => !IsPinned ? "" : PinSuspended ? "device DNS · suspended" : "device DNS";

    string _handshakeText = "";
    public string HandshakeText { get => _handshakeText; set => Set(ref _handshakeText, value); }

    bool _showHandshake;
    public bool ShowHandshake { get => _showHandshake; set => Set(ref _showHandshake, value); }

    public RelayCommand AddDomainCommand { get; }
    public RelayCommand RemoveDomainCommand { get; }
    public RelayCommand AddAllowedIpCommand { get; }
    public RelayCommand RemoveAllowedIpCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    public RelayCommand RemovePeerCommand { get; }

    void AddDomain()
    {
        var domain = NewDomain.Trim().TrimEnd('.');
        if (!IsValidDomain(domain) || _tunnel.Host.IsDomainInUse(domain, this))
        {
            DomainAddError = true;
            return;
        }
        Domains.Add(domain);
        NewDomain = "";
        if (!IsEditing)
            _tunnel.Host.DomainAdded(_tunnel, this, domain); // live: applies NRPT immediately
    }

    void RemoveDomain(string domain)
    {
        Domains.Remove(domain);
        if (!IsEditing)
            _tunnel.Host.DomainRemoved(_tunnel, this, domain);
    }

    void AddAllowedIp()
    {
        var cidr = NewAllowedIp.Trim();
        if (!Models.WireGuardConf.TryParseCidr(cidr, out _, out _))
        {
            AllowedIpAddError = true;
            return;
        }
        AllowedIps.Add(cidr);
        NewAllowedIp = "";
    }

    public string? Validate()
    {
        if (IsExternal)
            return HasDns && !IPAddress.TryParse(Dns.Trim(), out _) ? $"Invalid DNS server: {Dns}" : null;
        if (!IsValidKey(PublicKey)) return "Peer public key must be a valid 32-byte base64 key";
        if (!string.IsNullOrEmpty(PresharedKey) && !IsValidKey(PresharedKey)) return "Preshared key must be a valid 32-byte base64 key";
        if (!IsValidEndpoint(Endpoint)) return $"Endpoint must be host:port — got '{Endpoint}'";
        if (AllowedIps.Count == 0) return "Peer needs at least one allowed IP";
        foreach (var cidr in AllowedIps)
            if (!Models.WireGuardConf.TryParseCidr(cidr, out _, out _)) return $"Invalid allowed IP: {cidr}";
        if (HasDns && !IPAddress.TryParse(Dns.Trim(), out _)) return $"Invalid DNS server: {Dns}";
        foreach (var d in Domains)
            if (!IsValidDomain(d)) return $"Invalid domain: {d}";
        return null;
    }

    public string? DnsRouteWarning()
    {
        if (!HasDns || IsExternal || !IPAddress.TryParse(Dns.Trim(), out var ip)) return null;
        return AllowedIps.Any(c => Models.WireGuardConf.CidrContains(c, ip))
            ? null
            : $"DNS {Dns} is outside this peer's allowed IPs — queries would leak outside the tunnel";
    }

    public static bool IsValidDomain(string domain) =>
        domain.Length > 0 && DomainRegex().IsMatch(domain);

    public static bool IsValidKey(string key)
    {
        try { return Convert.FromBase64String(key.Trim()).Length == 32; }
        catch { return false; }
    }

    public static bool IsValidEndpoint(string endpoint)
    {
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0 || idx == endpoint.Length - 1) return false;
        return int.TryParse(endpoint[(idx + 1)..], out var port) && port is > 0 and <= 65535;
    }

    [GeneratedRegex(@"^(\*\.)?([A-Za-z0-9_-]+\.)*[A-Za-z0-9_-]+$")]
    private static partial Regex DomainRegex();
}
