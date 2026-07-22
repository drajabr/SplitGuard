using System.Security.Cryptography;
using SplitGuard.Services;

namespace SplitGuard;

// Windows head: WireGuardNT engine, NRPT split DNS, DPAPI keys, scheduled-task startup.
public class DesktopPlatform : IPlatform
{
    public string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SplitGuard");

    public IKeyProtector KeyProtector { get; } = new DpapiKeyProtector();

    public IQrScanner? CreateQrScanner() => new DesktopQrScanner(); // webcam, or a drop/paste fallback
    public ITunnelEngine CreateEngine() => new Services.TunnelManager();
    public ISplitDnsService CreateSplitDns() => new Services.NrptService();
    public IExternalTunnels? CreateExternalTunnels() => new Services.TunnelService();

    public bool SupportsStartup => true;
    public bool SupportsInstallerUpdate => true;
    public bool SupportsQrScan => true; // webcam capture; falls back to drop/paste when there's no camera

    public void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    public void SetStartOnBoot(bool on) => Services.StartupService.Set(on);

    public void SetSkipUacLaunch(bool on)
    {
        if (on) Services.StartupService.RegisterLaunchTask();
        else Services.StartupService.UnregisterLaunchTask();
    }
}

// Machine-scope DPAPI: any admin session on this machine can decrypt (the app runs
// elevated and the config lives in %ProgramData%).
public sealed class DpapiKeyProtector : IKeyProtector
{
    public string Protect(string base64Key)
    {
        var blob = ProtectedData.Protect(Convert.FromBase64String(base64Key), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string protectedBase64)
    {
        var raw = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(raw);
    }
}
