using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using WgSplitDns.ViewModels;
using WgSplitDns.Views;

namespace WgSplitDns;

public class App : Application
{
    bool _exiting;
    TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var vm = new MainViewModel(window);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.OnExit();

            // Close hides to tray; tunnels and DNS rules stay maintained while the app runs.
            window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                window.Hide();
            };
            SetupTray(desktop, window);
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        void ShowWindow()
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
        var show = new NativeMenuItem("Show");
        show.Click += (_, _) => ShowWindow();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            _exiting = true;
            _tray?.Dispose();
            desktop.Shutdown();
        };
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://WgSplitDns/Assets/app.ico"))),
            ToolTipText = "WG Split DNS",
            Menu = new NativeMenu { Items = { show, exit } },
        };
        _tray.Clicked += (_, _) => ShowWindow();
        _tray.IsVisible = true;
    }
}
