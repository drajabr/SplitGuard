namespace SplitGuard.Models;

public class AppConfig
{
    public int Version { get; set; } = 1;
    public PinnedDnsRef? PinnedDns { get; set; }
    public UiPrefs Ui { get; set; } = new();
    public List<TunnelConfig> Tunnels { get; set; } = new();
    public List<ExternalRuleConfig> Externals { get; set; } = new();
}

public class UiPrefs
{
    public string Theme { get; set; } = "auto";
    public string Accent { get; set; } = "blue";
    public string Font { get; set; } = "segoe";
    public string Zoom { get; set; } = "100%";
    public bool StartOnBoot { get; set; }
    public bool Notifications { get; set; } = true;
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
