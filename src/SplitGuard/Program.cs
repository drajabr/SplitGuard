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

        // Single-file publish extracts managed assemblies elsewhere; wireguard.dll ships beside the exe.
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) =>
        {
            if (name is "wireguard" or "wireguard.dll")
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var full = Path.Combine(dir, "wireguard.dll");
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
