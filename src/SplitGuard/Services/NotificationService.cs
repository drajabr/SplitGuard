using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SplitGuard.Services;

// Proper Windows 10/11 toast notifications attributed to "SplitGuard" with our icon.
// For an unpackaged app this needs an AppUserModelID (AUMID) that is:
//   1. set on our process, and
//   2. registered under HKCU\Software\Classes\AppUserModelId with a DisplayName + IconUri.
// The toast itself is shown through the WinRT ToastNotification API via PowerShell.
public static class NotificationService
{
    const string Aumid = "drajabr.splitguard";
    const string AppName = "SplitGuard";
    static string _iconPath = "";

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);

    // Call once at startup (and whenever the branding icon changes). Idempotent.
    public static void Register(string iconPath)
    {
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { }
        try
        {
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)) _iconPath = iconPath;
            using var key = Registry.CurrentUser.CreateSubKey($@"SOFTWARE\Classes\AppUserModelId\{Aumid}");
            key?.SetValue("DisplayName", AppName);
            if (_iconPath.Length > 0) key?.SetValue("IconUri", _iconPath);
        }
        catch { }
    }

    public static void Show(string title, string message, bool isError)
    {
        try
        {
            var t = XmlEscape(title);
            var m = XmlEscape(message);
            // Explicit appLogoOverride so our icon shows in the banner (the AUMID IconUri
            // alone is unreliable for unpackaged apps). src must be a file:/// URI.
            var logo = _iconPath.Length > 0
                ? $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"file:///{XmlEscape(_iconPath.Replace('\\', '/'))}\"/>"
                : "";
            // Single-quoted here-string keeps the XML literal; PowerShell -EncodedCommand
            // avoids all shell-quoting problems.
            var script =
                "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]|Out-Null\n" +
                "[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime]|Out-Null\n" +
                "$xml=@'\n" +
                $"<toast><visual><binding template=\"ToastGeneric\">{logo}<text>{t}</text><text>{m}</text></binding></visual></toast>\n" +
                "'@\n" +
                "$doc=New-Object Windows.Data.Xml.Dom.XmlDocument\n" +
                "$doc.LoadXml($xml)\n" +
                "$toast=New-Object Windows.UI.Notifications.ToastNotification $doc\n" +
                $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{Aumid}').Show($toast)\n";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch { /* notifications are best-effort */ }
    }

    static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
