using Microsoft.Win32;

namespace SplitGuard.Services;

// Run-at-logon via the per-user Run key. No admin needed to write it; because the
// app requires elevation, Windows will show a UAC prompt at logon when enabled.
public static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SplitGuard";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
