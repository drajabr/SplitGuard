using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia;

namespace SplitGuard;

static class Program
{
    static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        // Uninstaller hook: clear every trace we leave in the system (NRPT rules,
        // scheduled tasks), then exit. The uninstaller runs this elevated.
        if (args.Contains("--cleanup", StringComparer.OrdinalIgnoreCase))
        {
            Cleanup();
            return;
        }

        // The exe manifest is asInvoker; everything the app does needs admin, so a
        // non-elevated launch is redirected: run the registered launcher task (elevated,
        // no UAC prompt), else fall back to a normal UAC relaunch. Runs before the
        // single-instance logic so the stub never owns the mutex — the elevated copy's own
        // instance check handles "already running".
        if (!IsElevated())
        {
            // An elevated instance may already be running (the launcher task reports
            // "already running" as success): ask it to show its window and be done.
            if (SignalExistingInstance()) return;
            if (!Services.StartupService.RunLaunchTask() || !WaitForOtherInstance())
                RelaunchElevated(args);
            return;
        }

        // Single instance: a second launch signals the first to show its window, then exits.
        _singleInstance = new Mutex(true, @"Local\SplitGuardClient", out var createdNew);
        var showSignal = CreateShowSignal();
        if (!createdNew)
        {
            showSignal.Set();
            return;
        }
        new Thread(() =>
        {
            while (showSignal.WaitOne())
            {
                try { Avalonia.Threading.Dispatcher.UIThread.Post(() => App.ShowMainWindow?.Invoke()); }
                catch { }
            }
        })
        { IsBackground = true }.Start();

        // Single-file publish extracts managed assemblies elsewhere; wireguard.dll is expected
        // beside the exe, but we also probe a couple of fallback locations so local builds work.
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) =>
        {
            if (name is not ("wireguard" or "wireguard.dll")) return IntPtr.Zero;

            var candidates = new List<string>();
            var executableDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var baseDir = AppContext.BaseDirectory;

            foreach (var dir in new[] { executableDir, baseDir })
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                candidates.Add(Path.Combine(dir, "wireguard.dll"));
                candidates.Add(Path.Combine(dir, "native", "wireguard.dll"));
            }

            var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            candidates.Add(Path.Combine(repoRoot, "dist", "win-x64", "wireguard.dll"));
            candidates.Add(Path.Combine(repoRoot, "dist", "win-arm64", "wireguard.dll"));
            candidates.Add(Path.Combine(repoRoot, ".cache", "wireguard-nt", "wireguard.dll"));

            foreach (var full in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(full))
                    return NativeLibrary.Load(full);
            }

            return IntPtr.Zero;
        });
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // The elevated instance's default DACL only admits Administrators + SYSTEM, which the
    // stub's filtered token doesn't satisfy — grant Authenticated Users modify explicitly
    // so a non-elevated relaunch can pop the running window. Worst case for the loose ACL
    // is another local user making the window show.
    static EventWaitHandle CreateShowSignal()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
            AccessControlType.Allow));
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
            security.AddAccessRule(new EventWaitHandleAccessRule(identity.User,
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset,
            @"Local\SplitGuardClient.Show", out _, security);
    }

    static bool SignalExistingInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(@"Local\SplitGuardClient.Show", out var signal)) return false;
            using (signal) signal.Set();
            return true;
        }
        catch { return false; }
    }

    // The launcher task may point at a stale exe path; confirm an instance actually
    // appeared before trusting it. The elevated instance dedupes itself via the mutex.
    static bool WaitForOtherInstance()
    {
        var me = Environment.ProcessId;
        for (int i = 0; i < 20; i++)
        {
            if (Process.GetProcessesByName("SplitGuard").Any(p => p.Id != me)) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    static void RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { Process.Start(psi); }
        catch { } // user declined the UAC prompt: nothing to do
    }

    static void Cleanup()
    {
        try
        {
            var nrpt = new Services.NrptService();
            nrpt.RemoveAllTagged();
            nrpt.RemoveCatchAll();
        }
        catch { }
        Services.StartupService.Set(false);
        Services.StartupService.UnregisterLaunchTask();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
