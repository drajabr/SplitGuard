using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SplitGuard.Services;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class MainWindow : Window, IDialogs
{
    // Each theme is one coherent palette: page + card surface + chip fill + border
    // strength + secondary-text contrast, tuned together. null page/surface/item = the
    // OS-adaptive overlay path ("auto"). This folds the old separate "contrast" control in.
    record ThemeDef(string Name, ThemeVariant Variant, string? Page, string? Surface, string? Item,
                 double Dim, byte Hair, byte Field);

    // Surface palette registry (page + card + chip fill + borders + text contrast).
    static readonly ThemeDef[] Palettes =
    {
        new("auto",     ThemeVariant.Default, null,      null,      null,      0.68, 0x33, 0x40),
        new("light",    ThemeVariant.Light,  "#F4F3F0", "#FFFFFF", "#ECEAE4", 0.62, 0x33, 0x42),
        new("pearl",    ThemeVariant.Light,  "#EAE8E2", "#F7F5F0", "#E0DDD5", 0.62, 0x36, 0x46),
        new("slate",    ThemeVariant.Dark,   "#363C43", "#414750", "#2D333A", 0.72, 0x3E, 0x50),
        new("graphite", ThemeVariant.Dark,   "#24272B", "#2E3339", "#1F2226", 0.70, 0x38, 0x48),
        new("dark",     ThemeVariant.Dark,   "#1A1C1F", "#25282C", "#17191C", 0.70, 0x34, 0x44),
    };

    // Accent hue is its own control, independent of the surface theme (see Views.Accents).
    static (string Name, string Hex)[] AccentSteps => Accents.Steps;

    // Distinct UI fonts, applied to the whole window (including values/fields).
    static readonly (string Name, string Family)[] FontSteps =
    {
        ("segoe",       "Segoe UI Variable Text, Segoe UI"),           // modern sans (default)
        ("bahnschrift", "Bahnschrift, Segoe UI"),                      // condensed industrial
        ("georgia",     "Georgia, Cambria, serif"),                    // serif
        ("mono",        "Cascadia Mono, Consolas, Courier New, monospace"), // monospace
    };
    static readonly (string Name, double Scale)[] ZoomSteps =
    {
        ("100%", 1.0),
        ("125%", 1.25),
        ("150%", 1.5),
    };
    // Fonts and layout metrics scale together — no transforms, so wrapping stays correct.
    static readonly (string Key, double Base)[] ZoomResources =
    {
        ("Fs9", 9), ("Fs11", 11), ("Fs115", 11.5), ("Fs12", 12), ("Fs125", 12.5),
        ("Fs13", 13), ("Fs135", 13.5), ("Fs14", 14), ("Fs17", 17),
        ("CtrlH", 26), ("HeaderH", 30), ("CollapseH", 170),
    };

    int _themeIndex;
    int _accentIndex;
    int _fontIndex;
    int _zoomIndex;

    public MainWindow()
    {
        InitializeComponent();
        // The header sits inside the extended-client title-bar region (TitleBarHeightHint).
        // It has no background, so empty areas fall through to the OS caption and drag the
        // window natively; only the hit-testable header controls capture the pointer.
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, OnDrop);

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>();
        foreach (var item in files.OfType<IStorageFile>())
        {
            if (!item.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;
            await using var stream = await item.OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.AddTunnelFromText(await reader.ReadToEndAsync(), Path.GetFileNameWithoutExtension(item.Name));
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.N && vm is not null)
        {
            vm.CreateEmptyTunnel();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.E && vm is not null)
        {
            vm.Tunnels.FirstOrDefault(t => t.IsEditing)?.ToggleTextModeCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl+D: same two-step arming as the Delete button (first arms, second deletes).
        if (ctrl && e.Key == Key.D && vm is not null)
        {
            vm.Tunnels.FirstOrDefault(t => t.IsEditing)?.DeleteCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl+V outside a text field imports a config from the clipboard.
        if (ctrl && e.Key == Key.V
            && FocusManager?.GetFocusedElement() is not TextBox
            && vm is not null && Clipboard is not null)
        {
            var text = await Clipboard.GetTextAsync();
            if (MainViewModel.LooksLikeConfig(text))
            {
                vm.AddTunnelFromText(text!, null);
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    public void ApplyUiPrefs(SplitGuard.Models.UiPrefs prefs)
    {
        _themeIndex = Math.Max(0, Array.FindIndex(Palettes, p => p.Name == prefs.Theme));
        _accentIndex = Math.Max(0, Array.FindIndex(AccentSteps, a => a.Name == prefs.Accent));
        _fontIndex = Math.Max(0, Array.FindIndex(FontSteps, s => s.Name == prefs.Font));
        _zoomIndex = Math.Max(0, Array.FindIndex(ZoomSteps, s => s.Name == prefs.Zoom));
        ApplyTheme();   // also applies the accent (keeps "mono" in sync with the theme)
        ApplyFont();
        ApplyZoom();
        BuildMenus();
    }

    void ApplyFont()
    {
        var (name, family) = FontSteps[_fontIndex];
        FontFamily = new FontFamily(family);
    }

    void ApplyZoom()
    {
        var (name, scale) = ZoomSteps[_zoomIndex];
        var resources = Avalonia.Application.Current!.Resources;
        foreach (var (key, baseValue) in ZoomResources)
            resources[key] = baseValue * scale;
    }

    // Surface theme: page + card/chip fills + borders + text contrast.
    void ApplyTheme()
    {
        var t = Palettes[_themeIndex];
        var resources = Avalonia.Application.Current!.Resources;
        Avalonia.Application.Current!.RequestedThemeVariant = t.Variant;

        if (t.Page is null) ClearValue(BackgroundProperty);
        else Background = new SolidColorBrush(Color.Parse(t.Page));
        // Opaque surfaces when the palette defines them; transparent overlays under "auto".
        resources["SurfaceBrush"] = new SolidColorBrush(t.Surface is null ? Color.Parse("#00FFFFFF") : Color.Parse(t.Surface));
        resources["ItemBrush"] = new SolidColorBrush(t.Item is null ? Color.FromArgb(0x14, 0x80, 0x80, 0x80) : Color.Parse(t.Item));
        resources["DimOpacity"] = t.Dim;
        resources["HairlineBrush"] = new SolidColorBrush(Color.FromArgb(t.Hair, 0x80, 0x80, 0x80));
        resources["FieldBorderBrush"] = new SolidColorBrush(Color.FromArgb(t.Field, 0x80, 0x80, 0x80));
        ApplyAccent(); // re-resolve so a "mono" accent flips with the theme
    }

    // Effective light/dark, resolving the "auto" palette to the OS setting.
    ThemeVariant EffectiveVariant()
    {
        var v = Palettes[_themeIndex].Variant;
        if (v != ThemeVariant.Default) return v;
        return ActualThemeVariant == ThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    // Accent hue: brushes, Fluent's SystemAccent (toggles/focus/selection), and the icon.
    void ApplyAccent()
    {
        var (name, hex) = AccentSteps[_accentIndex];
        // "mono" = neutral accent that tracks the theme: white on dark, black on light.
        var color = hex.Length > 0
            ? Color.Parse(hex)
            : (EffectiveVariant() == ThemeVariant.Dark ? Color.Parse("#FFFFFF") : Color.Parse("#1A1A1A"));
        var resources = Avalonia.Application.Current!.Resources;
        resources["AccentBrush"] = new SolidColorBrush(color);
        resources["AccentDimBrush"] = new SolidColorBrush(color, 0.4);
        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorDark1"] = Shade(color, 0.85);
        resources["SystemAccentColorDark2"] = Shade(color, 0.70);
        resources["SystemAccentColorDark3"] = Shade(color, 0.55);
        resources["SystemAccentColorLight1"] = Tint(color, 0.15);
        resources["SystemAccentColorLight2"] = Tint(color, 0.30);
        resources["SystemAccentColorLight3"] = Tint(color, 0.45);

        // The icon sits on unknown backgrounds (taskbar/tray, light or dark), so a pure
        // white/black "mono" dragon would vanish. Use a neutral mid-gray for mono; the
        // colored accents are mid-tone and read fine on both.
        var iconColor = hex.Length > 0 ? color : Color.Parse("#8A93A0");
        var icons = AppIcons.Get(iconColor);
        Icon = icons.Idle;
        LogoImage.Source = icons.Logo;
        if (Avalonia.Application.Current is App app)
            app.SetAccentIcons(icons.Idle, icons.Active);
    }

    static Color Shade(Color c, double factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    static Color Tint(Color c, double factor) =>
        Color.FromRgb(
            (byte)(c.R + (255 - c.R) * factor),
            (byte)(c.G + (255 - c.G) * factor),
            (byte)(c.B + (255 - c.B) * factor));

    void Persist(Action<SplitGuard.Models.UiPrefs> update)
    {
        if (DataContext is not MainViewModel vm) return;
        update(vm.Prefs);
        vm.PersistPrefs();
    }

    // ---- native header menu (Add | View | Settings) ----------------------------

    static NativeMenuItem Action(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    // A submenu of options with the current one checked.
    NativeMenuItem Picker(string header, IEnumerable<string> options, int current, Action<int> select)
    {
        var root = new NativeMenuItem(header) { Menu = new NativeMenu() };
        var i = 0;
        foreach (var name in options)
        {
            var idx = i++;
            var item = new NativeMenuItem(name)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = idx == current,
            };
            item.Click += (_, _) => select(idx);
            root.Menu!.Items.Add(item);
        }
        return root;
    }

    void BuildMenus()
    {
        var mvm = DataContext as MainViewModel;
        var prefs = mvm?.Prefs;
        var root = new NativeMenu();

        var add = new NativeMenuItem("Add") { Menu = new NativeMenu() };
        add.Menu!.Items.Add(Action("Import .conf", () => _ = ImportConfAsync()));
        add.Menu.Items.Add(Action("New empty tunnel", () => (DataContext as MainViewModel)?.CreateEmptyTunnel()));
        add.Menu.Items.Add(new NativeMenuItemSeparator());
        add.Menu.Items.Add(Action("Rescan external tunnels", () => (DataContext as MainViewModel)?.RescanExternals()));
        root.Items.Add(add);

        var view = new NativeMenuItem("View") { Menu = new NativeMenu() };
        view.Menu!.Items.Add(Picker($"Theme: {Palettes[_themeIndex].Name}", Palettes.Select(p => p.Name), _themeIndex, i =>
        { _themeIndex = i; ApplyTheme(); Persist(p => p.Theme = Palettes[i].Name); BuildMenus(); }));
        view.Menu.Items.Add(Picker($"Accent: {AccentSteps[_accentIndex].Name}", AccentSteps.Select(a => a.Name), _accentIndex, i =>
        { _accentIndex = i; ApplyAccent(); Persist(p => p.Accent = AccentSteps[i].Name); BuildMenus(); }));
        view.Menu.Items.Add(Picker($"Font: {FontSteps[_fontIndex].Name}", FontSteps.Select(f => f.Name), _fontIndex, i =>
        { _fontIndex = i; ApplyFont(); Persist(p => p.Font = FontSteps[i].Name); BuildMenus(); }));
        view.Menu.Items.Add(Picker($"Zoom: {ZoomSteps[_zoomIndex].Name}", ZoomSteps.Select(z => z.Name), _zoomIndex, i =>
        { _zoomIndex = i; ApplyZoom(); Persist(p => p.Zoom = ZoomSteps[i].Name); BuildMenus(); }));
        root.Items.Add(view);

        var settings = new NativeMenuItem("Settings") { Menu = new NativeMenu() };
        var custom = new NativeMenuItem("Custom DNS forwarding")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = mvm?.HasCustomDns == true,
        };
        custom.Click += (_, _) => { mvm?.ToggleCustomDns(!(mvm?.HasCustomDns ?? false)); BuildMenus(); };
        settings.Menu!.Items.Add(custom);
        var boot = new NativeMenuItem("Start on Windows startup")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = prefs?.StartOnBoot == true,
        };
        boot.Click += (_, _) =>
        {
            var on = !(prefs?.StartOnBoot ?? false);
            StartupService.Set(on);
            Persist(p => p.StartOnBoot = on);
            BuildMenus();
        };
        settings.Menu.Items.Add(boot);
        var notif = new NativeMenuItem("Notifications")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = prefs?.Notifications == true,
        };
        notif.Click += (_, _) =>
        {
            var on = !(prefs?.Notifications ?? true);
            Persist(p => p.Notifications = on);
            BuildMenus();
        };
        settings.Menu.Items.Add(notif);
        root.Items.Add(settings);

        NativeMenu.SetMenu(this, root);
    }

    async Task ImportConfAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import WireGuard configuration",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("WireGuard config") { Patterns = new[] { "*.conf" } } },
        });
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.AddTunnelFromText(await reader.ReadToEndAsync(), Path.GetFileNameWithoutExtension(file.Name));
        }
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }

    Avalonia.Controls.Notifications.WindowNotificationManager? _notifier;

    public void Notify(string title, string message, bool isError)
    {
        _notifier ??= new Avalonia.Controls.Notifications.WindowNotificationManager(this)
        {
            Position = Avalonia.Controls.Notifications.NotificationPosition.BottomRight,
            MaxItems = 3,
        };
        _notifier.Show(new Avalonia.Controls.Notifications.Notification(
            title, message,
            isError ? Avalonia.Controls.Notifications.NotificationType.Error
                    : Avalonia.Controls.Notifications.NotificationType.Information));
    }
}
