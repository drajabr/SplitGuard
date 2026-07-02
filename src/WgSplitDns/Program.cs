using System.Runtime.InteropServices;
using Avalonia;

namespace WgSplitDns;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
