using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using SplitGuard.ViewModels;
using SplitGuard.Views;

namespace SplitGuard;

// Desktop head: builds the main window, tray icon/menu, close-hides-to-tray behavior,
// and the shutdown hooks. Program.cs installs Build as App.BuildHead.
public class TrayHost
{
    public static TrayHost? Current;

    bool _exiting;
    TrayIcon? _tray;
    MainViewModel? _vm;
    WindowIcon? _iconIdle;
    WindowIcon? _iconActive;
    Action? _exitAction;
    readonly HashSet<TunnelViewModel> _hooked = new();
    readonly Dictionary<TunnelViewModel, NativeMenuItem> _trayItems = new();

    public static void Build(App app)
    {
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var host = new TrayHost();
        Current = host;
        var window = new MainWindow();
        var vm = new MainViewModel(window, App.Platform!);
        host._vm = vm;
        window.DataContext = vm;
        window.ApplyUiPrefs(vm.Prefs);
        window.RestoreWindowBounds(vm.Prefs);
        desktop.MainWindow = window;
        desktop.ShutdownRequested += (_, _) =>
        {
            window.SaveWindowBounds(vm.Prefs);
            vm.PersistPrefs();
            vm.OnExit();
        };

        // Close hides to tray; tunnels and DNS rules stay maintained while the app runs.
        // Snapshot the window bounds on the way out so a later launch reopens there.
        window.Closing += (_, e) =>
        {
            window.SaveWindowBounds(vm.Prefs);
            vm.PersistPrefs();
            if (host._exiting) return;
            e.Cancel = true;
            window.Hide();
        };
        host.SetupTray(desktop, window);
        vm.Tunnels.CollectionChanged += host.OnTunnelsChanged;
        host.HookTunnels();
        // Keep the no-UAC launcher task registered (and pointing at the current exe).
        if (vm.Prefs.SkipUacLaunch && !Services.RuleStore.DemoMode)
            _ = Task.Run(Services.StartupService.RegisterLaunchTask);
        _ = vm.InitializeAsync();
    }

    void SetupTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        void ShowWindow()
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
        App.ShowMainWindow = ShowWindow;
        // Accent-composed icons are pushed via SetAccentIcons (ApplyUiPrefs runs first);
        // the static asset is only a fallback.
        _iconIdle ??= new WindowIcon(AssetLoader.Open(new Uri("avares://SplitGuard.Core/Assets/app.ico")));
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
        App.ExitApplication = _exitAction;
        RebuildTrayMenu();
        _tray.IsVisible = true;
    }

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

    // Two character columns, recomputed in RebuildTrayMenu: the name padded to the widest name
    // plus a gap (left-aligned), then the status right-aligned in the widest-status column, so
    // the RTT / handshake numbers all end flush at the right of the menu. (A "\t" in an Avalonia
    // native menu renders as a small tab gap, not a right-aligned accelerator column, so we pad
    // with spaces ourselves; proportional-font alignment isn't pixel-perfect but the numbers
    // read as one right-hand column.)
    int _trayNameCol = 12;   // longest name + gap (name field, left-aligned)
    int _trayStatusCol = 6;  // longest status (status field, right-aligned)

    // "office        23 ms" — name left, status right-aligned in the trailing column.
    // Connected + established: the ping RTT, else the last handshake as "14s". Anything else
    // (disconnected, connecting, external, no stats yet) is just "…".
    string TrayItemText(TunnelViewModel t) =>
        t.Name.PadRight(_trayNameCol) + TrayStatus(t).PadLeft(_trayStatusCol);

    static string TrayStatus(TunnelViewModel t)
    {
        if (!t.IsConnected || t.IsExternal || !t.IsEstablished) return "…";
        var rtt = t.Peers.Select(p => p.PingText).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (!string.IsNullOrEmpty(rtt)) return rtt;
        var hs = ShortAgo(t.Peers.Select(p => p.HandshakeText).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "");
        return string.IsNullOrEmpty(hs) ? "…" : hs;
    }

    // "handshake 14s ago" → "14s" for the tray's terse right column.
    static string ShortAgo(string handshakeText) =>
        handshakeText.Replace("handshake ", "").Replace(" ago", "").Replace("no handshake yet", "…");

    void RebuildTrayMenu()
    {
        if (_tray is null || _vm is null) return;
        var menu = new NativeMenu();
        _trayItems.Clear();
        // Size both columns to the widest name / status so the status column starts past the
        // longest name (a 6-char gap keeps it clearly a right-hand column, not adjacent text).
        var rows = _vm.Tunnels.Where(t => !t.IsCustom).ToList();
        if (rows.Count > 0)
        {
            _trayNameCol = rows.Max(t => t.Name.Length) + 6;
            _trayStatusCol = Math.Max(3, rows.Max(t => TrayStatus(t).Length));
        }
        foreach (var tunnel in _vm.Tunnels)
        {
            if (tunnel.IsCustom) continue; // the custom DNS card isn't a connection
            var item = new NativeMenuItem(TrayItemText(tunnel))
            {
                ToggleType = MenuItemToggleType.CheckBox,
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

        // App settings now live only in the in-app Settings panel (bottom bar), not the tray.
        var show = new NativeMenuItem("Show");
        show.Click += (_, _) => App.ShowMainWindow?.Invoke();
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
