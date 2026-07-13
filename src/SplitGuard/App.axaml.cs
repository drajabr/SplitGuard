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
    // Clean shutdown from anywhere (e.g. handing off to the update installer).
    public static Action? ExitApplication;

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
            if (_vm.Prefs.SkipUacLaunch && !Services.RuleStore.DemoMode)
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
        ExitApplication = _exitAction;
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
        // Live status (RTT / connecting / handshake) refreshes in place every stats tick —
        // the native menu reads current item headers when it opens.
        else if (e.PropertyName is nameof(TunnelViewModel.StatsTick) or nameof(TunnelViewModel.IsEstablished)
                 && sender is TunnelViewModel t)
            Dispatcher.UIThread.Post(() =>
            {
                if (_trayItems.TryGetValue(t, out var item)) item.Header = TrayItemText(t);
            });
    }

    // "office        23 ms" (tab-aligned to the right column, like an accelerator).
    // RTT when a healthcheck is running; otherwise the last handshake as "14s";
    // "connecting…" until established; "external" for official-client adapters.
    static string TrayItemText(TunnelViewModel t)
    {
        string status;
        if (t.IsExternal) status = t.IsConnected ? "external · up" : "external";
        else if (t.IsCustom) status = t.IsConnected ? "active" : "off";
        else if (!t.IsConnected) status = "off";
        else if (!t.IsEstablished) status = "connecting…";
        else
        {
            var rtt = t.Peers.Select(p => p.PingText).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            status = !string.IsNullOrEmpty(rtt) ? rtt : ShortAgo(t.Peers.Select(p => p.HandshakeText)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "");
        }
        return $"{t.Name}\t{status}";
    }

    // "handshake 14s ago" → "14s" for the tray's terse right column.
    static string ShortAgo(string handshakeText) =>
        handshakeText.Replace("handshake ", "").Replace(" ago", "").Replace("no handshake yet", "…");

    readonly Dictionary<TunnelViewModel, NativeMenuItem> _trayItems = new();

    void RebuildTrayMenu()
    {
        if (_tray is null || _vm is null) return;
        var menu = new NativeMenu();
        _trayItems.Clear();
        foreach (var tunnel in _vm.Tunnels)
        {
            if (tunnel.IsCustom) continue; // the custom DNS card isn't a connection
            var item = new NativeMenuItem(TrayItemText(tunnel))
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = tunnel.IsConnected,
                IsEnabled = !tunnel.IsExternal,
            };
            var captured = tunnel;
            item.Click += (_, _) => captured.IsConnected = !captured.IsConnected;
            menu.Items.Add(item);
            _trayItems[tunnel] = item;
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
        var updates = new NativeMenuItem("Check for updates on startup")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _vm.Prefs.CheckUpdates,
        };
        updates.Click += (_, _) =>
        {
            _vm.Prefs.CheckUpdates = !_vm.Prefs.CheckUpdates;
            _vm.PersistPrefs();
            RebuildTrayMenu();
        };
        settings.Menu.Items.Add(updates);
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
