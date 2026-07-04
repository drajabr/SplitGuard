using System.Diagnostics;

namespace SplitGuard.Services;

// Real Windows notifications (not in-app). Uses a short-lived NotifyIcon balloon, which
// Windows 10/11 surface as a system toast in the Action Center. Self-contained — no extra
// packages and no persistent second tray icon.
public static class NotificationService
{
    public static void Show(string title, string message, bool isError)
    {
        try
        {
            var kind = isError ? "Error" : "Info";
            var sysIcon = isError ? "Error" : "Information";
            var t = Escape(title);
            var m = Escape(message);
            var script =
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "Add-Type -AssemblyName System.Drawing;" +
                "$n = New-Object System.Windows.Forms.NotifyIcon;" +
                $"$n.Icon = [System.Drawing.SystemIcons]::{sysIcon};" +
                "$n.Visible = $true;" +
                $"$n.ShowBalloonTip(6000, '{t}', '{m}', [System.Windows.Forms.ToolTipIcon]::{kind});" +
                "Start-Sleep -Milliseconds 6500;" +
                "$n.Dispose()";
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch { /* notifications are best-effort */ }
    }

    static string Escape(string s) => s.Replace("'", "''");
}
