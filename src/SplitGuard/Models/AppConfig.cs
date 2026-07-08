namespace SplitGuard.Models;

public class AppConfig
{
    public int Version { get; set; } = 1;
    public PinnedDnsRef? PinnedDns { get; set; }
    public UiPrefs Ui { get; set; } = new();
    public List<TunnelConfig> Tunnels { get; set; } = new();
    public List<ExternalRuleConfig> Externals { get; set; } = new();
    // Externals the user deleted: skipped on rescan until they explicitly rescan.
    public List<string> DismissedExternals { get; set; } = new();
    // Optional standalone split-DNS card (NRPT rules unrelated to any WireGuard tunnel).
    public CustomDnsConfig? Custom { get; set; }
}

// A single "Custom DNS" card: NRPT domain→server rules applied while the app runs,
// independent of any tunnel connection. At most one exists.
public class CustomDnsConfig
{
    public List<CustomDnsRole> Roles { get; set; } = new();
    public string? Accent { get; set; }
    // Whether its NRPT rules are currently applied (toggled by the card's activate button).
    public bool Active { get; set; } = true;
}

public class CustomDnsRole
{
    // Stable id used to key this role's NRPT rules (acts as the "peer key").
    public string Id { get; set; } = "";
    public string? Dns { get; set; }
    public List<string> Domains { get; set; } = new();
}

public class UiPrefs
{
    public string Theme { get; set; } = "auto";
    public string Accent { get; set; } = "green";
    public string Font { get; set; } = "mono";
    public string Zoom { get; set; } = "100%";
    public bool StartOnBoot { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool CustomDnsEnabled { get; set; } = true;
    // Keep the trigger-less "SplitGuardLaunch" scheduled task registered so opening the
    // app doesn't show a UAC prompt.
    public bool SkipUacLaunch { get; set; } = true;
}

public class TunnelConfig
{
    public string Name { get; set; } = "";
    public string PrivateKeyProtected { get; set; } = "";
    public ushort ListenPort { get; set; }
    public List<string> Addresses { get; set; } = new();
    public List<PeerConfig> Peers { get; set; } = new();
    // Optional per-card accent hue name (null = use the global accent).
    public string? Accent { get; set; }
}

public class PeerConfig
{
    public string PublicKey { get; set; } = "";
    public string? PresharedKeyProtected { get; set; }
    public string Endpoint { get; set; } = "";
    public List<string> AllowedIps { get; set; } = new();
    public ushort PersistentKeepalive { get; set; }
    public string? Dns { get; set; }
    public List<string> Domains { get; set; } = new();
    // Optional in-tunnel IP pinged once per keepalive period while connected — generates
    // traffic (so handshakes stay fresh) and feeds failover health.
    public string? PingHost { get; set; }
    // Failover rank when this peer's allowed IPs overlap another connected peer's:
    // lower wins; equal ranks keep list order. 0 = default.
    public int Priority { get; set; }
}

// Domains attached to a tunnel managed by the official WireGuard client.
public class ExternalRuleConfig
{
    public string AdapterName { get; set; } = "";
    public string? Dns { get; set; }
    public List<string> Domains { get; set; } = new();
    public string? Accent { get; set; }
}

public class PinnedDnsRef
{
    public string TunnelName { get; set; } = "";
    public string PeerPublicKey { get; set; } = "";
}
