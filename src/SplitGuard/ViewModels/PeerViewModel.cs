using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;

namespace SplitGuard.ViewModels;

public partial class PeerViewModel : ObservableObject
{
    readonly TunnelViewModel _tunnel;

    public PeerViewModel(TunnelViewModel tunnel)
    {
        _tunnel = tunnel;
        AllowedIps.Add(new AddSlot());
        Domains.Add(new DnsSlot());
        Domains.Add(new AddSlot());
        AddDomainCommand = new RelayCommand(AddDomain);
        RemoveDomainCommand = new RelayCommand(p => RemoveDomain((string)p!));
        AddAllowedIpCommand = new RelayCommand(AddAllowedIp);
        RemoveAllowedIpCommand = new RelayCommand(p => AllowedIps.Remove((string)p!));
        TogglePinCommand = new RelayCommand(() => _tunnel.Host.TogglePin(_tunnel, this));
        RemovePeerCommand = new RelayCommand(() => _tunnel.RemovePeer(this));
        AllowedIps.CollectionChanged += (_, _) => { Raise(nameof(MetricEnabled)); Raise(nameof(MetricRankText)); };
        // DNS-only cards (external/custom) have only this body, so keep it open; real
        // WireGuard peers start collapsed so a multi-peer tunnel opens as a tidy list.
        _isExpanded = IsDnsOnly;
    }

    // Each peer body collapses to its header line (clicking the header toggles it,
    // like the tunnel card itself).
    bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    // Optional friendly name, editable in the header like the tunnel's own name.
    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _publicKey = "";
    public string PublicKey { get => _publicKey; set => Set(ref _publicKey, value); }

    string _presharedKey = "";
    public string PresharedKey { get => _presharedKey; set => Set(ref _presharedKey, value); }

    // Endpoint is edited as two fields (host + port) but stored/validated as "host:port".
    string _endpointHost = "";
    public string EndpointHost { get => _endpointHost; set => Set(ref _endpointHost, value); }

    string _endpointPort = "";
    public string EndpointPort { get => _endpointPort; set => Set(ref _endpointPort, value); }

    public string Endpoint
    {
        get => string.IsNullOrWhiteSpace(EndpointHost) ? "" : $"{EndpointHost.Trim()}:{EndpointPort.Trim()}";
        set
        {
            var i = value.LastIndexOf(':');
            if (i > 0) { EndpointHost = value[..i]; EndpointPort = value[(i + 1)..]; }
            else { EndpointHost = value; EndpointPort = ""; }
        }
    }

    string _dns = "";
    public string Dns
    {
        get => _dns;
        set { if (Set(ref _dns, value)) { Raise(nameof(HasDns)); Raise(nameof(PinEnabled)); } }
    }

    public bool HasDns => !string.IsNullOrWhiteSpace(Dns);
    public bool PinEnabled => HasDns;

    string _keepaliveText = "";
    public string KeepaliveText { get => _keepaliveText; set => Set(ref _keepaliveText, value); }

    public ushort ParsedKeepalive => ushort.TryParse(KeepaliveText.Trim(), out var v) ? v : (ushort)0;

    // Optional in-tunnel IP pinged once per keepalive period (25 s when keepalive is off):
    // keeps handshakes fresh and feeds failover health.
    string _pingHostText = "";
    public string PingHostText
    {
        get => _pingHostText;
        set { if (Set(ref _pingHostText, value)) Raise(nameof(HasPingHost)); }
    }

    // When a ping host is set it decides the peer's status, so the down/up/timeout knobs
    // are live and the header shows ping instead of the (now redundant) handshake.
    public bool HasPingHost => PingHostText.Trim().Length > 0;

    // How often to probe, in seconds; blank = default (5 s). Only meaningful with a ping host.
    string _pingPeriodText = "";
    public string PingPeriodText { get => _pingPeriodText; set => Set(ref _pingPeriodText, value); }

    public int ParsedPingPeriod => int.TryParse(PingPeriodText.Trim(), out var v) ? v : 0;

    // Per-ping timeout in seconds; blank = default (3 s). Only meaningful with a ping host.
    string _pingTimeoutText = "";
    public string PingTimeoutText { get => _pingTimeoutText; set => Set(ref _pingTimeoutText, value); }

    public int ParsedPingTimeout => int.TryParse(PingTimeoutText.Trim(), out var v) ? v : 0;

    // Consecutive ping failures that flip health down; blank = default (3).
    string _pingDownText = "";
    public string PingDownText { get => _pingDownText; set => Set(ref _pingDownText, value); }

    public int ParsedPingDown => int.TryParse(PingDownText.Trim(), out var v) ? v : 0;

    // Consecutive ping successes that flip health back up; blank = default (3).
    string _pingUpText = "";
    public string PingUpText { get => _pingUpText; set => Set(ref _pingUpText, value); }

    public int ParsedPingUp => int.TryParse(PingUpText.Trim(), out var v) ? v : 0;

    // Failover rank (0-10) for overlapping allowed IPs: lower wins; peers in the same
    // route group must use distinct values (checked when connecting).
    string _metricText = "";
    public string MetricText
    {
        get => _metricText;
        set { if (Set(ref _metricText, value)) Raise(nameof(MetricRankText)); }
    }

    public int ParsedMetric => int.TryParse(MetricText.Trim(), out var v) ? v : 0;

    // Metric only matters inside a route group — greyed out until this peer's allowed
    // IPs actually overlap another peer's. Refreshed when this peer's list changes.
    public bool MetricEnabled => _tunnel.Host.HasRouteGroup(this);

    // The consequence of the number, spelled out: "→ 1st of 2 for 10.7.0.0/24 · lower wins".
    public string MetricRankText
    {
        get
        {
            if (IsDnsOnly) return "";
            if (_tunnel.Host.RouteGroupInfo(this) is not { } g)
                return "no overlap with another peer — unused";
            return $"→ {Ordinal(g.Position)} of {g.Size} for {g.Cidr} · lower wins";
        }
    }

    static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    // Strings plus a trailing AddSlot (the inline "+" box).
    public ObservableCollection<object> AllowedIps { get; } = new();
    public ObservableCollection<object> Domains { get; } = new();

    public IEnumerable<string> AllowedIpValues => AllowedIps.OfType<string>();
    public IEnumerable<string> DomainValues => Domains.OfType<string>();

    public static void Fill(ObservableCollection<object> list, IEnumerable<string> values)
    {
        list.Clear();
        foreach (var v in values) list.Add(v);
        list.Add(new AddSlot());
    }

    // The domains list leads with the inline DNS box so chips flow around it.
    public static void FillDomains(ObservableCollection<object> list, IEnumerable<string> values)
    {
        list.Clear();
        list.Add(new DnsSlot());
        foreach (var v in values) list.Add(v);
        list.Add(new AddSlot());
    }

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
    public bool IsCustom => _tunnel.IsCustom;
    // DNS-only rows: externals and the custom card show just DNS + Domains.
    public bool IsDnsOnly => IsExternal || IsCustom;
    public bool ShowWgFields => !IsDnsOnly;
    public string BlockTitle => IsCustom ? "DNS rule" : "Peer";

    bool _isPinned;
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    bool _pinSuspended;
    public bool PinSuspended { get => _pinSuspended; set => Set(ref _pinSuspended, value); }

    string _handshakeText = "";
    public string HandshakeText { get => _handshakeText; set { if (Set(ref _handshakeText, value)) Raise(nameof(ShowHandshake)); } }

    // Peer card header shows the last handshake on the right (RTT sits on the left).
    public bool ShowHandshake => HandshakeText.Trim().Length > 0;

    string _pingText = "";
    public string PingText { get => _pingText; set => Set(ref _pingText, value); }

    // Cumulative transfer totals (collapsed status line, left side) and the last-handshake
    // shown next to the peer name. HasStats gates the status line on a live connection.
    string _txTotalText = "";
    public string TxTotalText { get => _txTotalText; set => Set(ref _txTotalText, value); }

    string _rxTotalText = "";
    public string RxTotalText { get => _rxTotalText; set => Set(ref _rxTotalText, value); }

    bool _hasStats;
    public bool HasStats { get => _hasStats; set => Set(ref _hasStats, value); }

    // "active" / "standby" while this peer's allowed IPs overlap another connected peer's.
    string _failoverRole = "";
    public string FailoverRole { get => _failoverRole; set => Set(ref _failoverRole, value); }

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
        Domains.Insert(Domains.Count - 1, domain);
        NewDomain = "";
    }

    void RemoveDomain(string domain) => Domains.Remove(domain);

    void AddAllowedIp()
    {
        var cidr = Models.WireGuardConf.NormalizeCidr(NewAllowedIp);
        if (!Models.WireGuardConf.TryParseCidr(cidr, out _, out _))
        {
            AllowedIpAddError = true;
            return;
        }
        AllowedIps.Insert(AllowedIps.Count - 1, cidr);
        NewAllowedIp = "";
    }

    public string? DnsRouteWarning()
    {
        if (!HasDns || IsDnsOnly || !IPAddress.TryParse(Dns.Trim(), out var ip)) return null;
        return AllowedIpValues.Any(c => Models.WireGuardConf.CidrContains(c, ip))
            ? null
            : $"DNS {Dns} is outside this peer's allowed IPs — queries would leak outside the tunnel";
    }

    public string? PingRouteWarning()
    {
        if (IsDnsOnly || !IPAddress.TryParse(PingHostText.Trim(), out var ip)) return null;
        return AllowedIpValues.Any(c => Models.WireGuardConf.CidrContains(c, ip))
            ? null
            : $"Ping host {PingHostText.Trim()} is outside this peer's allowed IPs — probes wouldn't test the tunnel";
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
