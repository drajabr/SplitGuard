using System.Diagnostics;

namespace SplitGuard.Services;

// Scheduled-task plumbing for two things an elevation-requiring app needs:
//  - run at logon elevated ("SplitGuard", ONLOGON, RL HIGHEST)
//  - launch on demand without a UAC prompt ("SplitGuardLaunch", trigger-less, RL HIGHEST):
//    a non-elevated stub just runs the task, and Task Scheduler starts the app elevated.
// Registering either task requires elevation once; after that, no prompts.
public static class StartupService
{
    const string TaskName = "SplitGuard";
    const string LaunchTaskName = "SplitGuardLaunch";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled() => TaskExists(TaskName);

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

    // ---- UAC-skip launcher task --------------------------------------------------

    public static bool LaunchTaskExists() => TaskExists(LaunchTaskName);

    // schtasks can't create a trigger-less task and its /SD date format is locale-bound,
    // so registration goes through Register-ScheduledTask. ExecutionTimeLimit zero means
    // "never kill the app". Re-registered on every elevated start so a moved/updated exe
    // path heals itself.
    public static void RegisterLaunchTask()
    {
        if (string.IsNullOrWhiteSpace(ExePath)) return;
        var exe = ExePath.Replace("'", "''");
        RunPowerShell(
            $"$a = New-ScheduledTaskAction -Execute '{exe}';" +
            "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero);" +
            $"Register-ScheduledTask -TaskName '{LaunchTaskName}' -Action $a -Settings $s -RunLevel Highest -Force | Out-Null");
    }

    public static void UnregisterLaunchTask()
    {
        try
        {
            using var p = StartSchtasks("/Delete", "/TN", LaunchTaskName, "/F");
            p.WaitForExit(15000);
        }
        catch { }
    }

    // Returns true when Task Scheduler accepted the run; the app itself starts a moment later.
    public static bool RunLaunchTask()
    {
        try
        {
            using var p = StartSchtasks("/Run", "/TN", LaunchTaskName);
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    static bool TaskExists(string name)
    {
        try
        {
            using var process = StartSchtasks("/Query", "/TN", name, "/FO", "CSV", "/NH");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    static void RunPowerShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command })
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
        }
        catch { }
    }
}
