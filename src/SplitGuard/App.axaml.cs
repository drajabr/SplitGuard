using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using SplitGuard.ViewModels;
using SplitGuard.Views;

namespace SplitGuard;

public class App : Application
{
    public static Action? ShowMainWindow;

    bool _exiting;
    TrayIcon? _tray;
    MainViewModel? _vm;
    WindowIcon? _iconIdle;
    WindowIcon? _iconActive;
    readonly HashSet<TunnelViewModel> _hooked = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            _vm = new MainViewModel(window);
            window.DataContext = _vm;
            window.ApplyUiPrefs(_vm.Prefs);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => _vm.OnExit();

            // Close hides to tray; tunnels and DNS rules stay maintained while the app runs.
            window.Closing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                window.Hide();
            };
            SetupTray(desktop, window);
            _vm.Tunnels.CollectionChanged += OnTunnelsChanged;
            HookTunnels();
            // Keep the no-UAC launcher task registered (and pointing at the current exe).
            if (_vm.Prefs.SkipUacLaunch)
                _ = Task.Run(Services.StartupService.RegisterLaunchTask);
            _ = _vm.InitializeAsync();
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
        ShowMainWindow = ShowWindow;
        // Accent-composed icons are pushed via SetAccentIcons (ApplyUiPrefs runs first);
        // the static asset is only a fallback.
        _iconIdle ??= new WindowIcon(AssetLoader.Open(new Uri("avares://SplitGuard/Assets/app.ico")));
        _iconActive ??= _iconIdle;
        _tray = new TrayIcon
        {
            Icon = _iconIdle,
            ToolTipText = "SplitGuard",
        };
        _tray.Clicked += (_, _) => ShowWindow();
        _exitAction = () =>
        {
            _exiting = true;
            _tray?.Dispose();
            desktop.Shutdown();
        };
        RebuildTrayMenu();
        _tray.IsVisible = true;
    }

    Action? _exitAction;

    public void SetAccentIcons(WindowIcon idle, WindowIcon active)
    {
        _iconIdle = idle;
        _iconActive = active;
        if (_tray is not null) RebuildTrayMenu();
    }

    void OnTunnelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HookTunnels();
        RebuildTrayMenu();
    }

    void HookTunnels()
    {
        if (_vm is null) return;
        // Unhook tunnels that left the collection (deleted / external vanished) so their
        // handlers and references don't leak for the process lifetime.
        foreach (var gone in _hooked.Where(t => !_vm.Tunnels.Contains(t)).ToList())
        {
            gone.PropertyChanged -= OnTunnelPropertyChanged;
            _hooked.Remove(gone);
        }
        foreach (var t in _vm.Tunnels)
        {
            if (!_hooked.Add(t)) continue;
            t.PropertyChanged += OnTunnelPropertyChanged;
        }
    }

    void OnTunnelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TunnelViewModel.IsConnected) or nameof(TunnelViewModel.Name))
            Dispatcher.UIThread.Post(RebuildTrayMenu);
    }

    void RebuildTrayMenu()
    {
        if (_tray is null || _vm is null) return;
        var menu = new NativeMenu();
        foreach (var tunnel in _vm.Tunnels)
        {
            if (tunnel.IsCustom) continue; // the custom DNS card isn't a connection
            var item = new NativeMenuItem(tunnel.Name)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = tunnel.IsConnected,
                IsEnabled = !tunnel.IsExternal,
            };
            var captured = tunnel;
            item.Click += (_, _) => captured.IsConnected = !captured.IsConnected;
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new NativeMenuItemSeparator());

        // App settings live here in the tray.
        var settings = new NativeMenuItem("Settings") { Menu = new NativeMenu() };
        var custom = new NativeMenuItem("Custom DNS forwarding")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _vm.HasCustomDns,
        };
        custom.Click += (_, _) => { _vm.ToggleCustomDns(!_vm.HasCustomDns); RebuildTrayMenu(); };
        settings.Menu!.Items.Add(custom);
        var boot = new NativeMenuItem("Start on Windows startup")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _vm.Prefs.StartOnBoot,
        };
        boot.Click += (_, _) =>
        {
            var on = !_vm.Prefs.StartOnBoot;
            SplitGuard.Services.StartupService.Set(on);
            _vm.Prefs.StartOnBoot = on;
            _vm.PersistPrefs();
            RebuildTrayMenu();
        };
        settings.Menu.Items.Add(boot);
        var skipUac = new NativeMenuItem("Skip UAC prompt on launch")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _vm.Prefs.SkipUacLaunch,
        };
        skipUac.Click += (_, _) =>
        {
            var on = !_vm.Prefs.SkipUacLaunch;
            _vm.Prefs.SkipUacLaunch = on;
            _vm.PersistPrefs();
            _ = Task.Run(() =>
            {
                if (on) Services.StartupService.RegisterLaunchTask();
                else Services.StartupService.UnregisterLaunchTask();
            });
            RebuildTrayMenu();
        };
        settings.Menu.Items.Add(skipUac);
        var notif = new NativeMenuItem("Notifications")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _vm.Prefs.Notifications,
        };
        notif.Click += (_, _) =>
        {
            _vm.Prefs.Notifications = !_vm.Prefs.Notifications;
            _vm.PersistPrefs();
            RebuildTrayMenu();
        };
        settings.Menu.Items.Add(notif);
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());

        var show = new NativeMenuItem("Show");
        show.Click += (_, _) => ShowMainWindow?.Invoke();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => _exitAction?.Invoke();
        menu.Items.Add(show);
        menu.Items.Add(exit);
        _tray.Menu = menu;

        var connected = _vm.Tunnels.Where(t => t.IsConnected && !t.IsCustom).ToList();
        _tray.Icon = connected.Count > 0 ? _iconActive : _iconIdle;
        _tray.ToolTipText = connected.Count > 0
            ? $"SplitGuard — {string.Join(", ", connected.Select(t => t.Name))}"
            : "SplitGuard";
    }
}
