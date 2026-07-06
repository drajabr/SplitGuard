using System.Diagnostics;

namespace SplitGuard.Services;

// Run-at-logon via a per-user scheduled task so the app can launch elevated at logon.
// This is the reliable mechanism for a Windows app that requires elevation.
public static class StartupService
{
    const string TaskName = "SplitGuard";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var process = StartSchtasks("/Query", "/TN", TaskName, "/FO", "CSV", "/NH");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(ExePath)) return;
                StartSchtasks(
                    "/Create",
                    "/TN", TaskName,
                    "/SC", "ONLOGON",
                    "/TR", $"\"{ExePath}\"",
                    "/RL", "HIGHEST",
                    "/DELAY", "0001:00",
                    "/F");
            }
            else
            {
                StartSchtasks("/Delete", "/TN", TaskName, "/F");
            }
        }
        catch { }
    }

    static Process StartSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }
}
