using Android.Content;
using Android.Net;

namespace SplitGuard.Droid;

// System DNS discovery while our VPN is up: the ACTIVE network is the VPN itself, so
// walk all networks and take the DNS servers of the non-VPN one with internet.
static class AndroidDns
{
    // Last non-empty discovery. The live walk can transiently return nothing (network
    // revalidating, app backgrounded); falling back to the servers we last saw keeps
    // unruled queries resolving instead of blackholing all DNS. Seeded at connect time,
    // before the VPN network exists (see SgVpnService.Establish).
    static volatile List<string> _lastKnown = new();

    public static List<string> UnderlyingServers()
    {
        var result = new List<string>();
        try
        {
            var cm = (ConnectivityManager?)Android.App.Application.Context
                .GetSystemService(Context.ConnectivityService);
            if (cm is null) return _lastKnown;
            foreach (var network in cm.GetAllNetworks())
            {
                var caps = cm.GetNetworkCapabilities(network);
                if (caps is null
                    || caps.HasTransport(TransportType.Vpn)
                    || !caps.HasCapability(NetCapability.Internet)) continue;
                var props = cm.GetLinkProperties(network);
                if (props?.DnsServers is null) continue;
                foreach (var addr in props.DnsServers)
                {
                    var s = addr.HostAddress;
                    if (!string.IsNullOrEmpty(s) && !result.Contains(s)) result.Add(s);
                }
            }
        }
        catch { }
        if (result.Count > 0) _lastKnown = result;
        return result.Count > 0 ? result : _lastKnown;
    }
}
