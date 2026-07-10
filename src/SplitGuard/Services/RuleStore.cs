using System.Security.Cryptography;
using System.Text.Json;
using SplitGuard.Models;

namespace SplitGuard.Services;

public class RuleStore
{
    // UI review harness (SplitGuard --ui-demo): canned in-memory config, no persistence,
    // no elevation, no NRPT/scheduled-task side effects. For eyeballing layout only.
    public static bool DemoMode;

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SplitGuard");
    static readonly string ConfigPath = Path.Combine(Dir, "config.json");

    static RuleStore()
    {
        // One-time migration from the pre-rename data folder.
        try
        {
            var old = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgSplitDns");
            if (!Directory.Exists(Dir) && Directory.Exists(old))
                Directory.Move(old, Dir);
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
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
        }
        catch
        {
            // Corrupt config: start fresh rather than crash; the old file is preserved.
            try { File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true); } catch { }
        }
        return new AppConfig();
    }

    public void Save(AppConfig config)
    {
        if (DemoMode) return;
        Directory.CreateDirectory(Dir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOpts));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static string Protect(string base64Key)
    {
        var blob = ProtectedData.Protect(Convert.FromBase64String(base64Key), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(blob);
    }

    public static string Unprotect(string protectedBase64)
    {
        var raw = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(raw);
    }

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
                            Metric = 1,
                        },
                        new PeerConfig
                        {
                            Name = "backup",
                            PublicKey = Pub(),
                            Endpoint = "backup.office.example.com:51820",
                            AllowedIps = { "10.7.0.0/24" },
                            PersistentKeepalive = 25,
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
