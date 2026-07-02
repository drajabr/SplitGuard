using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WgSplitDns.Services;

// Snapshot of the physical adapters' DNS servers — the tail of the catch-all chain.
public static class SystemDns
{
    public static List<string> Snapshot()
    {
        var servers = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var dns in nic.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = dns.ToString();
                    if (!servers.Contains(s)) servers.Add(s);
                }
            }
        }
        catch { }
        return servers;
    }
}
