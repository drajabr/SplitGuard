using Avalonia;
using Avalonia.Markup.Xaml;
using SplitGuard.Services;

namespace SplitGuard;

// Shared application shell: styles/resources only. Each head (Windows desktop, Android)
// installs its platform and a lifetime-specific builder before the framework starts.
public class App : Application
{
    public static Action? ShowMainWindow;
    // Clean shutdown from anywhere (e.g. handing off to the update installer).
    public static Action? ExitApplication;

    // Set by the head's entry point before AppBuilder starts the framework.
    public static IPlatform? Platform;
    public static Action<App>? BuildHead;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        BuildHead?.Invoke(this);
        base.OnFrameworkInitializationCompleted();
    }
}
