using System.Text.Json;
using SplitGuard.Models;

namespace SplitGuard.Services;

public class RuleStore
{
    // UI review harness (SplitGuard --ui-demo): canned in-memory config, no persistence,
    // no elevation, no NRPT/scheduled-task side effects. For eyeballing layout only.
    public static bool DemoMode;

    // Key at-rest protection, installed once by the platform head (DPAPI on Windows).
    // Static because Protect/Unprotect are called from view models and the tunnel engine
    // without a store instance in hand.
    public static IKeyProtector Protector = new PassThroughProtector();

    readonly string _dir;
    readonly string _configPath;

    public RuleStore(string dir)
    {
        _dir = dir;
        _configPath = Path.Combine(_dir, "config.json");
        // One-time migration from the pre-rename data folder (no-op off Windows).
        try
        {
            var old = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgSplitDns");
            if (!Directory.Exists(_dir) && Directory.Exists(old))
                Directory.Move(old, _dir);
        }
        catch { }
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AppConfig Load()
    {
        if (DemoMode) return DemoConfig();
        try
        {
            if (File.Exists(_configPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath), JsonOpts) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config: start fresh rather than crash; the old file is preserved.
            try { File.Copy(_configPath, _configPath + ".bad", overwrite: true); } catch { }
        }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        if (DemoMode) return;
        Directory.CreateDirectory(_dir);
        var tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        File.Move(tmp, _configPath, overwrite: true);
    }

    public static string Protect(string base64Key) => Protector.Protect(base64Key);

    public static string Unprotect(string protectedBase64) => Protector.Unprotect(protectedBase64);

    // A representative config exercising every UI element: two tunnels, multi-peer with
    // overlapping allowed IPs (failover group), ping settings, DNS + domains, long values.
    static AppConfig DemoConfig()
    {
        string Key() => Protect(Convert.ToBase64String(Curve25519.GeneratePrivateKey()));
        string Pub() => Convert.ToBase64String(Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey()));
        return new AppConfig
        {
            Ui = new UiPrefs { CustomDnsEnabled = false },
            Tunnels =
            {
                new TunnelConfig
                {
                    Name = "office",
                    PrivateKeyProtected = Key(),
                    Addresses = { "10.7.0.2/32" },
                    Peers =
                    {
                        new PeerConfig
                        {
                            Name = "main",
                            PublicKey = Pub(),
                            Endpoint = "vpn.office.example.com:51820",
                            AllowedIps = { "10.7.0.0/24", "192.168.10.0/24" },
                            PersistentKeepalive = 25,
                            Dns = "10.7.0.1",
                            Domains = { "*.corp.example", "*.lab.internal" },
                            PingHost = "10.7.0.1",
                            PingPeriod = 5,
                            Metric = 1,
                        },
                        new PeerConfig
                        {
                            Name = "backup",
                            PublicKey = Pub(),
                            Endpoint = "backup.office.example.com:51820",
                            AllowedIps = { "10.7.0.0/24" },
                            PersistentKeepalive = 25,
                            // Same domain as "main": a contested domain group — the active
                            // claimant's pill gets the "· active" accent in the detail.
                            Dns = "10.7.0.6",
                            Domains = { "*.corp.example" },
                            PingHost = "10.7.0.5",
                            PingDownCount = 3,
                            PingUpCount = 5,
                            PingTimeout = 2,
                            Metric = 2,
                        },
                    },
                },
                new TunnelConfig
                {
                    Name = "homelab",
                    PrivateKeyProtected = Key(),
                    Addresses = { "10.99.0.7/32" },
                    Peers =
                    {
                        new PeerConfig
                        {
                            PublicKey = Pub(),
                            Endpoint = "home.example.net:51820",
                            AllowedIps = { "10.99.0.0/16" },
                            Dns = "10.99.0.1",
                            Domains = { "*.home.arpa" },
                        },
                    },
                },
            },
        };
    }
}
