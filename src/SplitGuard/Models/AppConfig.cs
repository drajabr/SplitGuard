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
    // Check GitHub for a newer release on startup (at most once a day). On by default.
    public bool CheckUpdates { get; set; } = true;
    // ISO-8601 UTC timestamp of the last automatic update check (empty = never).
    public string LastUpdateCheck { get; set; } = "";
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
    // Whether the user last had this tunnel turned on; restored at startup (mirrors
    // CustomDnsConfig.Active). Default false so existing configs never auto-connect.
    public bool Connected { get; set; }
}

public class PeerConfig
{
    // Optional friendly name shown in the peer header (cosmetic only).
    public string? Name { get; set; }
    public string PublicKey { get; set; } = "";
    public string? PresharedKeyProtected { get; set; }
    public string Endpoint { get; set; } = "";
    public List<string> AllowedIps { get; set; } = new();
    public ushort PersistentKeepalive { get; set; }
    public string? Dns { get; set; }
    public List<string> Domains { get; set; } = new();
    // Optional in-tunnel IP pinged once per keepalive period while connected — generates
    // traffic (so handshakes stay fresh) and, when set, decides failover health:
    // PingDownCount consecutive failures (PingTimeout seconds each) = down,
    // PingUpCount consecutive successes = up. Without one, handshake freshness decides.
    public string? PingHost { get; set; }
    // Per-ping timeout in seconds (1-60); 0 = default (3 s).
    public int PingTimeout { get; set; }
    // How often to probe, in seconds (1-3600); 0 = default (5 s).
    public int PingPeriod { get; set; }
    // Consecutive ping failures to flip health down (1-100); 0 = default (3).
    public int PingDownCount { get; set; }
    // Consecutive ping successes to flip health back up (1-100); 0 = default (3).
    // Separate from down so recovery can be judged more cautiously than failure.
    public int PingUpCount { get; set; }
    // Failover rank (0-10) when this peer's allowed IPs overlap another connected peer's:
    // lower wins; peers in a route group must use distinct values (checked at connect).
    public int Metric { get; set; }
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
