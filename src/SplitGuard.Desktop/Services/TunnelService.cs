using System.Net.NetworkInformation;

namespace SplitGuard.Services;

// Detects WireGuard adapters managed by the official client (read-only for us).
public class TunnelService : IExternalTunnels
{
    public event Action? AdaptersChanged;

    public TunnelService()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public List<ExternalAdapter> GetExternalAdapters()
    {
        var result = new List<ExternalAdapter>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!nic.Description.Contains("WireGuard Tunnel", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new ExternalAdapter(nic.Name, nic.OperationalStatus == OperationalStatus.Up));
            }
        }
        catch { }
        return result;
    }

    void OnNetworkChanged(object? s, EventArgs e) => AdaptersChanged?.Invoke();
    void OnAvailabilityChanged(object? s, NetworkAvailabilityEventArgs e) => AdaptersChanged?.Invoke();

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }
}
