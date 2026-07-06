using System.Runtime.InteropServices;
using Avalonia;

namespace SplitGuard;

static class Program
{
    static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance: a second launch signals the first to show its window, then exits.
        _singleInstance = new Mutex(true, @"Local\SplitGuardClient", out var createdNew);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SplitGuardClient.Show");
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
