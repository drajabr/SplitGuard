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

    ParcelFileDescriptor? _tun;
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
        var builder = new Builder(this);
        builder.SetSession(cfg.Name).SetMtu(1280);
        foreach (var addr in cfg.Addresses)
            if (WireGuardConf.TryParseCidr(addr, out var ip, out var prefix))
                builder.AddAddress(ip.ToString(), prefix);
        var routes = new HashSet<string>();
        foreach (var p in cfg.Peers)
            foreach (var cidr in p.AllowedIps)
                if (WireGuardConf.TryParseCidr(cidr, out var net, out var prefix)
                    && routes.Add($"{net}/{prefix}"))
                    builder.AddRoute(net.ToString(), prefix);
        // Tunnel-wide DNS from the config (per-domain routing arrives with the Phase 4
        // forwarder; until then this matches stock WireGuard behavior).
        foreach (var dns in cfg.Peers.Where(p => !string.IsNullOrWhiteSpace(p.Dns))
                     .Select(p => p.Dns!.Trim()).Distinct())
            try { builder.AddDnsServer(dns); } catch { }

        _tun = builder.Establish() ?? throw new InvalidOperationException("VPN establish failed (consent revoked?)");
        _name = cfg.Name;
        var handle = WgGo.TurnOn(cfg.Name, _tun.DetachFd(), Uapi.BuildSettings(cfg));
        if (handle < 0)
            throw new InvalidOperationException($"wireguard-go failed to start (code {handle})");
        Handle = handle;
        // Protect the encrypted-UDP sockets so they bypass the VPN (mandatory: without
        // this the handshake packets route back into the tunnel and nothing connects).
        var v4 = WgGo.SocketV4(handle); if (v4 >= 0) Protect(v4);
        var v6 = WgGo.SocketV6(handle); if (v6 >= 0) Protect(v6);
    }

    void TearDown()
    {
        if (Handle >= 0)
        {
            try { WgGo.TurnOff(Handle); } catch { }
            Handle = -1;
        }
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
