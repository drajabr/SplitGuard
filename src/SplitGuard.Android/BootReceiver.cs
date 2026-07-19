using System.Linq;
using Android.Content;
using SplitGuard.Services;

namespace SplitGuard.Droid;

// Reconnect the tunnel the user last had on, after a device reboot (or an app update). Gated on the
// "Start on boot" pref. Runs headless — no Activity — so it only works when VPN consent is already
// granted (it can't prompt for it); an ungranted device just no-ops until the app is opened.
[BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        if (intent?.Action != Intent.ActionBootCompleted && intent?.Action != Intent.ActionMyPackageReplaced) return;
        try
        {
            var cfg = new RuleStore(context.FilesDir!.AbsolutePath).Load();
            if (!cfg.Ui.StartOnBoot) return;
            // Single-tunnel VpnService: reconnect the one that was on (first, if several were marked).
            var tunnel = cfg.Tunnels.FirstOrDefault(t => t.Connected);
            if (tunnel is null) return;
            // Consent must already be granted — a receiver can't show the consent dialog.
            if (Android.Net.VpnService.Prepare(context) is not null) return;

            SgVpnService.SplitDnsEnabled = cfg.Ui.AndroidSplitDns;
            SgVpnService.PendingConfig = tunnel;
            var svc = new Intent(context, typeof(SgVpnService)).SetAction(SgVpnService.ActionConnect);
            context.StartForegroundService(svc);
        }
        catch { /* boot-time best-effort — never crash the boot broadcast */ }
    }
}
