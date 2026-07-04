using Avalonia;
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
        InitChrome();
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

    // ---- header popovers: Add + Settings (plain buttons, no menus) --------------

    Flyout? _addFlyout;
    Flyout? _settingsFlyout;

    void InitChrome()
    {
        _addFlyout ??= new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        _addFlyout.Content = BuildAddPanel();
        AddButton.Flyout = _addFlyout;

        _settingsFlyout ??= new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        _settingsFlyout.Content = BuildSettingsPanel();
        SettingsButton.Flyout = _settingsFlyout;
    }

    void RefreshSettings()
    {
        if (_settingsFlyout is not null) _settingsFlyout.Content = BuildSettingsPanel();
    }

    Control BuildAddPanel()
    {
        var sp = new StackPanel { Spacing = 1, MinWidth = 190 };
        sp.Children.Add(FlatItem("Import .conf…", () => _ = ImportConfAsync()));
        sp.Children.Add(FlatItem("New empty tunnel", () => (DataContext as MainViewModel)?.CreateEmptyTunnel()));
        sp.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        sp.Children.Add(FlatItem("Rescan external tunnels", () => (DataContext as MainViewModel)?.RescanExternals()));
        return sp;
    }

    Button FlatItem(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5),
        };
        b.Click += (_, _) => { _addFlyout?.Hide(); onClick(); };
        return b;
    }

    Control BuildSettingsPanel()
    {
        var mvm = DataContext as MainViewModel;
        var prefs = mvm?.Prefs;
        var sp = new StackPanel { Spacing = 7, MinWidth = 300, Margin = new Thickness(4) };

        sp.Children.Add(Section("APPEARANCE"));
        sp.Children.Add(Row("Theme", Segmented(Palettes.Select(p => p.Name), _themeIndex, i =>
        { _themeIndex = i; ApplyTheme(); Persist(p => p.Theme = Palettes[i].Name); RefreshSettings(); })));
        sp.Children.Add(Row("Accent", Swatches(_accentIndex, i =>
        { _accentIndex = i; ApplyAccent(); Persist(p => p.Accent = AccentSteps[i].Name); RefreshSettings(); })));
        sp.Children.Add(Row("Font", Segmented(FontSteps.Select(f => f.Name), _fontIndex, i =>
        { _fontIndex = i; ApplyFont(); Persist(p => p.Font = FontSteps[i].Name); RefreshSettings(); })));
        sp.Children.Add(Row("Zoom", Segmented(ZoomSteps.Select(z => z.Name), _zoomIndex, i =>
        { _zoomIndex = i; ApplyZoom(); Persist(p => p.Zoom = ZoomSteps[i].Name); RefreshSettings(); })));

        sp.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        sp.Children.Add(Section("BEHAVIOR"));
        sp.Children.Add(Check("Custom DNS forwarding", mvm?.HasCustomDns == true, on =>
        { mvm?.ToggleCustomDns(on); RefreshSettings(); }));
        sp.Children.Add(Check("Start on Windows startup", prefs?.StartOnBoot == true, on =>
        { StartupService.Set(on); Persist(p => p.StartOnBoot = on); }));
        sp.Children.Add(Check("Notifications", prefs?.Notifications == true, on =>
        { Persist(p => p.Notifications = on); }));
        return sp;
    }

    static Control Section(string text) => new TextBlock
    {
        Text = text, FontSize = 10, Opacity = 0.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 0, 0),
    };

    Control Row(string label, Control content)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*") };
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        l.Classes.Add("lbl");
        Grid.SetColumn(content, 1);
        g.Children.Add(l);
        g.Children.Add(content);
        return g;
    }

    Control Segmented(IEnumerable<string> options, int current, Action<int> select)
    {
        var wp = new WrapPanel();
        var i = 0;
        foreach (var name in options)
        {
            var idx = i++;
            var b = new Button { Content = name, Margin = new Thickness(0, 0, 4, 4) };
            b.Classes.Add("seg");
            if (idx == current) b.Classes.Add("sel");
            b.Click += (_, _) => select(idx);
            wp.Children.Add(b);
        }
        return wp;
    }

    Control Swatches(int current, Action<int> select)
    {
        var wp = new WrapPanel();
        var isDark = EffectiveVariant() == ThemeVariant.Dark;
        var i = 0;
        foreach (var (name, _) in AccentSteps)
        {
            var idx = i++;
            var color = Accents.Resolve(name, isDark);
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(color) };
            var b = new Button
            {
                Content = dot,
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 4, 4),
                Background = Brushes.Transparent,
                MinHeight = 0,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(idx == current ? 2 : 0),
                BorderBrush = new SolidColorBrush(color),
            };
            b.Click += (_, _) => select(idx);
            wp.Children.Add(b);
        }
        return wp;
    }

    Control Check(string text, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = text, IsChecked = value };
        cb.IsCheckedChanged += (_, _) => set(cb.IsChecked == true);
        return cb;
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
