namespace SplitGuard.Services;

// ---- platform seams --------------------------------------------------------------
// The shared library talks to the OS only through these. The Windows head implements
// them with WireGuardNT/NRPT/DPAPI/scheduled tasks; the Android head with a
// VpnService-backed engine, an in-tunnel DNS forwarder, and app-private storage.

// Per-peer live signals published every poll tick. FailoverRole: null = not part of an
// overlap group, otherwise "active" / "standby".
public record PeerLive(double UpBps, double DownBps, DateTime? Handshake,
    double? PingMs, bool? PingOk, bool Healthy, string? FailoverRole,
    double? AvgPingMs, double? PingLoss, ulong TotalTx, ulong TotalRx);

public record TunnelStats(Dictionary<string, PeerLive> PerPeer);

// A WireGuard adapter managed by something else (the official Windows client).
public record ExternalAdapter(string Name, bool IsUp);

// One split-DNS rule as the backend reports it (NRPT rule on Windows, forwarder
// table entry on Android).
public record NrptRule(string Id, string[] Namespaces, string[] Servers);

// Establishes/tears down WireGuard tunnels and publishes live stats.
public interface ITunnelEngine : IDisposable
{
    bool IsConnected(string name);
    void Connect(Models.TunnelConfig config);
    void Disconnect(string name);
    void DisconnectAll();
    event Action<string, TunnelStats>? StatsUpdated;
    event Action<string>? FailoverChanged;
    // The engine tore a tunnel down on its own (single-tunnel platforms replacing the
    // active tunnel, or the OS revoking the VPN) — the UI must flip the card off.
    event Action<string>? Disconnected;
}

// Watches for externally-managed WireGuard adapters. Windows-only concept.
public interface IExternalTunnels : IDisposable
{
    event Action? AdaptersChanged;
    List<ExternalAdapter> GetExternalAdapters();
}

// Per-domain DNS routing (NRPT semantics): domain rules point at a peer's DNS server,
// one "." catch-all chains pinned → tunnel → system DNS.
public interface ISplitDnsService
{
    // True when an outside policy (GPO on Windows) owns DNS rules and ours won't apply.
    bool IsPolicyManaged { get; }
    void ApplyDomain(string tunnelName, string peerPublicKey, string domain, string dnsServer);
    void ApplyPeerRules(string tunnelName, string peerPublicKey, IEnumerable<string> domains, string dnsServer);
    void RemovePeerRules(string tunnelName, string peerPublicKey);
    void RemoveByTunnel(string tunnelName);
    void SetCatchAll(string[] orderedServers);
    void RemoveCatchAll();
    List<NrptRule> GetTaggedRules();
    void RemoveTagged(IEnumerable<string> ids);
    void RemoveAllTagged();
}

// Rule identity + namespace forms shared by every backend (and by the view model's
// reconcile pass), so ids stay identical across platforms.
public static class SplitDnsRules
{
    public static string DomainToNamespace(string domain) =>
        domain.StartsWith("*.") ? domain[1..] : domain; // "*.x" → ".x" (subdomain-inclusive), bare stays exact

    public static string RuleId(string tunnelName, string peerPublicKey, string domain) =>
        $"WGSDNS|{tunnelName}|{Short(peerPublicKey)}|{domain}";

    public static string Short(string key) => key.Length > 8 ? key[..8] : key;
}

// At-rest protection for private/preshared keys (DPAPI on Windows; Android relies on
// app-private storage + file-based encryption, so it passes through).
public interface IKeyProtector
{
    string Protect(string base64Key);
    string Unprotect(string protectedBase64);
}

public sealed class PassThroughProtector : IKeyProtector
{
    public string Protect(string base64Key) => base64Key;
    public string Unprotect(string protectedBase64) => protectedBase64;
}

// A live camera QR scanner surfaced inside the app UI (a drawer card, not a screen).
// The preview is an Avalonia control the host builds; Decoded fires with the raw text of
// a scanned code (a WireGuard .conf) on the UI thread.
public interface IQrScanner : IDisposable
{
    // The live preview control to drop into the QR drawer.
    Avalonia.Controls.Control Preview { get; }
    event Action<string>? Decoded;
    event Action<string>? Failed;   // permission denied / camera error (message for a toast)
    void Start();
    void Stop();
}

// Everything the shared UI/view models need from the host platform.
public interface IPlatform
{
    // Camera QR import (Android). Null where there's no camera scan flow (desktop).
    IQrScanner? CreateQrScanner();

    string ConfigDirectory { get; }
    IKeyProtector KeyProtector { get; }
    ITunnelEngine CreateEngine();
    ISplitDnsService CreateSplitDns();
    IExternalTunnels? CreateExternalTunnels(); // null where the concept doesn't exist

    // Feature gates for platform-only UI (rows/flows are hidden, not disabled).
    bool SupportsStartup { get; }          // run-at-boot + UAC-skip launcher (Windows)
    bool SupportsInstallerUpdate { get; }  // download-and-run installer self-update
    bool SupportsSplitDnsToggle { get; }   // Android: in-tunnel forwarder on/off fallback
    bool SupportsQrScan { get; }           // camera "Scan QR code" add flow
    bool SupportsBootStart => false;       // Android: reconnect the last tunnel after a reboot

    void SetStartOnBoot(bool on);
    void SetSkipUacLaunch(bool on);
    void SetSplitDnsEnabled(bool on);      // applies on the NEXT connect

    // Tint the OS system bars (Android status/navigation bars) to match the app theme's page
    // background, with dark glyphs on a light background. No-op where the OS chrome isn't ours.
    void SetSystemBarColor(uint argb, bool lightBackground) { }
}
