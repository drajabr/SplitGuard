using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Com.Wireguard.Android.Backend;
using SplitGuard.Models;
using SplitGuard.Services;

namespace SplitGuard.Droid;

// The single VPN tunnel. We own the VpnService (instead of using GoBackend's) so the
// tun fd stays in our hands — the Phase 4 split-DNS relay splices in between the tun
// and wireguard-go. One tunnel at a time (platform ceiling without root).
[Service(
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSystemExempted)]
[IntentFilter(new[] { "android.net.VpnService" })]
public class SgVpnService : Android.Net.VpnService
{
    public const string ActionConnect = "energy.saba.splitguard.CONNECT";
    public const string ActionDisconnect = "energy.saba.splitguard.DISCONNECT";
    const int NotificationId = 1;
    const string Channel = "vpn";

    // In-process handoff from the engine (service and app share the process).
    public static TunnelConfig? PendingConfig;
    public static event Action<string, string?>? StateChanged; // (tunnelName, error) — error null = up
    public static event Action<string>? Stopped;               // tunnel torn down (any reason)

    public static SgVpnService? Instance;
    public static string? ActiveTunnel;
    public static int Handle = -1;

    // Per-domain DNS routing (the relay + forwarder). Off = stock behavior: tun fd
    // handed straight to wireguard-go, DNS from the conf. Mirrors UiPrefs.AndroidSplitDns.
    public static volatile bool SplitDnsEnabled = true;

    ParcelFileDescriptor? _tun;
    TunPacketRelay? _relay;
    string? _name;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionConnect when PendingConfig is not null:
                var cfg = PendingConfig;
                PendingConfig = null;
                StartForeground(NotificationId, BuildNotification(cfg.Name),
                    Android.Content.PM.ForegroundService.TypeSystemExempted);
                try
                {
                    TearDown(); // replace any running tunnel
                    Establish(cfg);
                    ActiveTunnel = cfg.Name;
                    StateChanged?.Invoke(cfg.Name, null);
                }
                catch (Exception ex)
                {
                    StateChanged?.Invoke(cfg.Name, ex.Message);
                    StopSelfClean();
                }
                return StartCommandResult.Sticky;

            case ActionDisconnect:
            default:
                StopSelfClean();
                return StartCommandResult.NotSticky;
        }
    }

    void Establish(TunnelConfig cfg)
    {
        // Resolve peer endpoint hostnames NOW, before the tun exists — once builder.Establish()
        // installs the tun routes, a DNS lookup would be routed into a tunnel that has no live
        // wireguard/forwarder yet and would hang until it times out, failing the connect.
        var uapi = Uapi.BuildSettings(cfg);
        // Snapshot the underlying network's DNS while it's still the active network — the
        // forwarder falls back to this if the live walk comes up empty mid-connection.
        _ = AndroidDns.UnderlyingServers();

        var builder = new Builder(this);
        builder.SetSession(cfg.Name).SetMtu(1280);
        // Every builder call names the offending value on failure — the VpnService.Builder throws a
        // bare "Bad address" with no context, which is impossible to diagnose from the config alone.
        foreach (var addr in cfg.Addresses)
        {
            if (!WireGuardConf.TryParseCidr(addr, out var ip, out var prefix))
                throw new InvalidOperationException($"Interface address '{addr}' isn't a valid IP/CIDR");
            try { builder.AddAddress(ip.ToString(), prefix); }
            catch (Exception ex) { throw new InvalidOperationException($"Interface address {addr}: {ex.Message}"); }
        }
        var routes = new HashSet<string>();
        foreach (var p in cfg.Peers)
            foreach (var cidr in p.AllowedIps)
            {
                if (!WireGuardConf.TryParseCidr(cidr, out var net, out var prefix))
                    throw new InvalidOperationException($"Peer '{p.Name}' AllowedIP '{cidr}' isn't a valid CIDR");
                // Mask host bits: addRoute rejects "10.7.0.1/24" as a "Bad address" (Windows
                // masks it silently), which otherwise fails the whole connect on import.
                net = WireGuardConf.MaskNetwork(net, prefix);
                if (routes.Add($"{net}/{prefix}"))
                    try { builder.AddRoute(net.ToString(), prefix); }
                    catch (Exception ex) { throw new InvalidOperationException($"AllowedIP {cidr} (route {net}/{prefix}): {ex.Message}"); }
            }
        var split = SplitDnsEnabled;
        if (split)
        {
            // All DNS goes to the in-tunnel forwarder: the OS resolver sends queries to
            // the virtual IP, the relay peels them off the tun and answers per-domain
            // (peer DNS through the tunnel, system DNS around it) with NRPT semantics.
            try { builder.AddDnsServer(DnsForwarder.VirtualDns); builder.AddRoute(DnsForwarder.VirtualDns, 32); }
            catch (Exception ex) { throw new InvalidOperationException($"DNS forwarder {DnsForwarder.VirtualDns}: {ex.Message}"); }
        }
        else
        {
            // Stock behavior: tunnel-wide DNS straight from the config. A bad DNS entry names itself.
            foreach (var dns in cfg.Peers.Where(p => !string.IsNullOrWhiteSpace(p.Dns))
                         .Select(p => p.Dns!.Trim()).Distinct())
                try { builder.AddDnsServer(dns); }
                catch (Exception ex) { throw new InvalidOperationException($"DNS server {dns}: {ex.Message}"); }
        }

        _tun = builder.Establish() ?? throw new InvalidOperationException("VPN establish failed (consent revoked?)");
        _name = cfg.Name;
        int wgFd;
        if (split)
        {
            // The relay takes ownership of the tun fd (detaches it from the PFD) and gives
            // wireguard-go the socketpair end.
            _relay = new TunPacketRelay(_tun, new DnsForwarder(this));
            _tun = null;
            wgFd = _relay.WgEndFd;
        }
        else
        {
            wgFd = _tun.DetachFd();
            _tun = null; // wireguard-go owns it now
        }
        var handle = WgGo.TurnOn(cfg.Name, wgFd, uapi);
        if (handle < 0)
            throw new InvalidOperationException($"wireguard-go failed to start (code {handle})");
        Handle = handle;
        // Protect the encrypted-UDP sockets so they bypass the VPN (mandatory: without
        // this the handshake packets route back into the tunnel and nothing connects).
        var v4 = WgGo.SocketV4(handle); if (v4 >= 0) Protect(v4);
        var v6 = WgGo.SocketV6(handle); if (v6 >= 0) Protect(v6);
        _relay?.Start();
    }

    void TearDown()
    {
        if (Handle >= 0)
        {
            try { WgGo.TurnOff(Handle); } catch { }
            Handle = -1;
        }
        try { _relay?.Dispose(); } catch { }
        _relay = null;
        try { _tun?.Close(); } catch { }
        _tun = null;
        if (_name is not null) { var n = _name; _name = null; ActiveTunnel = null; Stopped?.Invoke(n); }
    }

    void StopSelfClean()
    {
        TearDown();
        try { StopForeground(StopForegroundFlags.Remove); } catch { }
        StopSelf();
    }

    // The OS revoked the VPN (another VPN app took the slot, or the user killed it).
    public override void OnRevoke()
    {
        StopSelfClean();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        TearDown();
        Instance = null;
        base.OnDestroy();
    }

    Notification BuildNotification(string tunnel)
    {
        var mgr = (NotificationManager)GetSystemService(NotificationService)!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            mgr.CreateNotificationChannel(new NotificationChannel(Channel, "VPN", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(this, 0,
            PackageManager!.GetLaunchIntentForPackage(PackageName!),
            PendingIntentFlags.Immutable);
        return new Notification.Builder(this, Channel)
            .SetContentTitle("SplitGuard")!
            .SetContentText($"{tunnel} connected")!
            .SetSmallIcon(Resource.Drawable.icon)!
            .SetOngoing(true)!
            .SetContentIntent(open)!
            .Build()!;
    }
}
