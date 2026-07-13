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

    // Light/dark switch: "auto" follows the OS (adaptive overlay), "light" and "graphite"
    // force the tone. The header button cycles auto → light → graphite.
    static readonly ThemeDef[] Palettes =
    {
        new("auto",     ThemeVariant.Default, null,      null,      null,      0.68, 0x33, 0x40),
        new("light",    ThemeVariant.Light,  "#F4F3F0", "#FFFFFF", "#ECEAE4", 0.62, 0x33, 0x42),
        new("graphite", ThemeVariant.Dark,   "#24272B", "#2E3339", "#1F2226", 0.70, 0x38, 0x48),
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
        ("Fs13", 13), ("Fs135", 13.5), ("Fs14", 14), ("Fs15", 15), ("Fs17", 17),
        ("CtrlH", 26), ("HeaderH", 38), ("CollapseH", 170),
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

        // Enter in a single-line field commits and drops focus (clearing the highlight).
        // Add-boxes bind Enter to their add command and mark it handled, so they never
        // reach here; multiline (raw-config) fields keep Enter for newlines.
        if (e.Key == Key.Enter && !e.Handled
            && FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: false })
        {
            MainScroll.Focus();
            e.Handled = true;
            return;
        }
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
        // Reconcile the run-at-boot registry entry with the (possibly default-on) pref.
        try { StartupService.Set(prefs.StartOnBoot); } catch { }
    }

    void ApplyFont()
    {
        var (_, family) = FontSteps[_fontIndex];
        FontFamily = new FontFamily(family);
    }

    void ApplyZoom()
    {
        var (_, scale) = ZoomSteps[_zoomIndex];
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
        // Soft neutral fill behind borderless fields; a touch stronger on hover.
        resources["FieldFillBrush"] = new SolidColorBrush(Color.FromArgb(t.Name == "light" ? (byte)0x14 : (byte)0x1E, 0x80, 0x80, 0x80));
        resources["FieldFillHoverBrush"] = new SolidColorBrush(Color.FromArgb(t.Name == "light" ? (byte)0x20 : (byte)0x2E, 0x80, 0x80, 0x80));
        // Menus/popups need an opaque backing (cards may be translucent overlays under "auto").
        var menuBg = t.Surface is not null
            ? Color.Parse(t.Surface)
            : (EffectiveVariant() == ThemeVariant.Light ? Color.Parse("#FBFBFB") : Color.Parse("#2B2F34"));
        resources["MenuSurfaceBrush"] = new SolidColorBrush(menuBg);
        // Keys consumed by the Fluent MenuFlyoutPresenter/MenuItem (used by the tray menu on
        // Windows): neutral hover wash + stable item text so it doesn't repaint on hover.
        resources["MenuFlyoutPresenterBackground"] = new SolidColorBrush(menuBg);
        resources["MenuFlyoutPresenterBorderBrush"] = resources["HairlineBrush"];
        resources["MenuFlyoutItemBackgroundPointerOver"] = resources["ItemBrush"];
        resources["MenuFlyoutItemBackgroundPressed"] = resources["ItemBrush"];
        var menuFg = new SolidColorBrush(EffectiveVariant() == ThemeVariant.Light ? Color.Parse("#1B2420") : Color.Parse("#E4E9E6"));
        resources["MenuFlyoutItemForeground"] = menuFg;
        resources["MenuFlyoutItemForegroundPointerOver"] = menuFg;
        resources["MenuFlyoutItemForegroundPressed"] = menuFg;
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
        var (_, hex) = AccentSteps[_accentIndex];
        // "mono" = neutral accent that tracks the theme: white on dark, black on light.
        var color = hex.Length > 0
            ? Color.Parse(hex)
            : (EffectiveVariant() == ThemeVariant.Dark ? Color.Parse("#FFFFFF") : Color.Parse("#1A1A1A"));
        var resources = Avalonia.Application.Current!.Resources;
        resources["AccentBrush"] = new SolidColorBrush(color);
        resources["AccentDimBrush"] = new SolidColorBrush(color, 0.4);
        // Text/glyphs on accent fills — mono's white accent needs dark text, not white.
        resources["OnAccentBrush"] = new SolidColorBrush(Accents.On(color));
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

        // Save a branded PNG and register it as the toast-notification app icon/name.
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SplitGuard");
            Directory.CreateDirectory(dir);
            var iconPath = Path.Combine(dir, "notify.png");
            icons.Logo.Save(iconPath);
            NotificationService.Register(iconPath);
        }
        catch { }
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

    // ---- header controls: cycling view buttons + floating Add popover -----------

    Flyout? _addFlyout;
    Flyout? _menuFlyout;

    void InitChrome()
    {
        // The floating Add button opens the add-options popover above itself. Right-edge
        // aligned: the button sits at the window's right border, so a centered popover
        // would spill outside the window.
        _addFlyout ??= new Flyout { Placement = PlacementMode.TopEdgeAlignedRight };
        _addFlyout.Content = BuildAddPanel();
        AddButton.Flyout = _addFlyout;
    }

    void OnUpdateClick(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.OnUpdateButtonClicked();

    // The logo opens the unified menu: Add actions, the tray-side settings, and appearance
    // pickers — one themed flyout. Content is rebuilt each open so it reflects tray changes.
    void OnLogoClick(object? sender, RoutedEventArgs e)
    {
        _menuFlyout ??= new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _menuFlyout.Content = BuildMenuPanel();
        _menuFlyout.ShowAt(LogoButton);
    }

    Control BuildMenuPanel()
    {
        var root = new StackPanel { Spacing = 1, MinWidth = 248 };

        root.Children.Add(SectionHeader("Add", first: true));
        root.Children.Add(FlatItem("Import .conf…", () => _ = ImportConfAsync()));
        root.Children.Add(FlatItem("New empty tunnel", () => (DataContext as MainViewModel)?.CreateEmptyTunnel()));
        root.Children.Add(FlatItem("Rescan external tunnels", () => (DataContext as MainViewModel)?.RescanExternals()));

        if (DataContext is MainViewModel vm)
        {
            root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
            root.Children.Add(SectionHeader("Settings"));
            root.Children.Add(ToggleRow("Custom DNS forwarding", vm.HasCustomDns, on => vm.ToggleCustomDns(on)));
            root.Children.Add(ToggleRow("Start on Windows startup", vm.Prefs.StartOnBoot, on =>
            {
                SplitGuard.Services.StartupService.Set(on);
                vm.Prefs.StartOnBoot = on; vm.PersistPrefs();
            }));
            root.Children.Add(ToggleRow("Skip UAC prompt on launch", vm.Prefs.SkipUacLaunch, on =>
            {
                vm.Prefs.SkipUacLaunch = on; vm.PersistPrefs();
                _ = Task.Run(() =>
                {
                    if (on) SplitGuard.Services.StartupService.RegisterLaunchTask();
                    else SplitGuard.Services.StartupService.UnregisterLaunchTask();
                });
            }));
            root.Children.Add(ToggleRow("Notifications", vm.Prefs.Notifications, on => { vm.Prefs.Notifications = on; vm.PersistPrefs(); }));
            root.Children.Add(ToggleRow("Check for updates on startup", vm.Prefs.CheckUpdates, on => { vm.Prefs.CheckUpdates = on; vm.PersistPrefs(); }));
        }

        root.Children.Add(new Separator { Margin = new Thickness(0, 4) });
        root.Children.Add(SectionHeader("Appearance"));
        root.Children.Add(OptionBlock("Theme", System.Array.ConvertAll(Palettes, p => p.Name), _themeIndex, SelectTheme));
        root.Children.Add(OptionBlock("Accent", System.Array.ConvertAll(AccentSteps, a => a.Name), _accentIndex, SelectAccent));
        root.Children.Add(OptionBlock("Font", System.Array.ConvertAll(FontSteps, s => s.Name), _fontIndex, SelectFont));
        root.Children.Add(OptionBlock("Zoom", System.Array.ConvertAll(ZoomSteps, s => s.Name), _zoomIndex, SelectZoom));
        return root;
    }

    static Control SectionHeader(string text, bool first = false) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 10.5, FontWeight = FontWeight.SemiBold, Opacity = 0.5,
        Margin = new Thickness(8, first ? 4 : 9, 8, 3),
    };

    Button ToggleRow(string label, bool isOn, Action<bool> onToggle)
    {
        var accent = this.FindResource("AccentBrush") as IBrush ?? Brushes.Gray;
        var check = new TextBlock
        {
            Classes = { "glyph" }, Text = "", FontSize = 12, Width = 18,
            Margin = new Thickness(0, 0, 6, 0), Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center, Opacity = isOn ? 1 : 0,
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var state = isOn;
        var btn = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5), CornerRadius = new Avalonia.CornerRadius(5),
        };
        btn.Click += (_, _) => { state = !state; check.Opacity = state ? 1 : 0; onToggle(state); };
        return btn;
    }

    Control OptionBlock(string label, string[] names, int current, Action<int> pick)
    {
        var stack = new StackPanel { Margin = new Thickness(8, 2, 8, 5), Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, Opacity = 0.55, FontSize = 11, Margin = new Thickness(2, 0, 0, 1) });
        var wrap = new WrapPanel();
        var buttons = new List<Button>();
        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;
            var seg = new Button { Content = names[i], Classes = { "seg" }, Margin = new Thickness(0, 0, 4, 4) };
            if (i == current) seg.Classes.Add("sel");
            seg.Click += (_, _) =>
            {
                foreach (var x in buttons) x.Classes.Remove("sel");
                seg.Classes.Add("sel");
                pick(idx);
            };
            buttons.Add(seg);
            wrap.Children.Add(seg);
        }
        stack.Children.Add(wrap);
        return stack;
    }

    void SelectTheme(int i) { _themeIndex = i; ApplyTheme(); Persist(p => p.Theme = Palettes[i].Name); }
    void SelectAccent(int i) { _accentIndex = i; ApplyAccent(); Persist(p => p.Accent = AccentSteps[i].Name); }
    void SelectFont(int i) { _fontIndex = i; ApplyFont(); Persist(p => p.Font = FontSteps[i].Name); }
    void SelectZoom(int i) { _zoomIndex = i; ApplyZoom(); Persist(p => p.Zoom = ZoomSteps[i].Name); }

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
        b.Click += (_, _) => { _addFlyout?.Hide(); _menuFlyout?.Hide(); onClick(); };
        return b;
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

    public void Notify(string title, string message, bool isError) =>
        NotificationService.Show(title, message, isError);
}
