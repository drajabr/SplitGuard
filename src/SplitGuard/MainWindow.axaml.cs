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
        ("Fs13", 13), ("Fs135", 13.5), ("Fs14", 14), ("Fs17", 17),
        ("CtrlH", 26), ("HeaderH", 38), ("CollapseH", 170),
    };

    // Theme-switch icons (Material Design light_mode / dark_mode / contrast, 24×24) — one
    // consistent family so auto, light and dark read as siblings.
    const string SunGeometry = "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13l2 0c.55 0 1-.45 1-1s-.45-1-1-1l-2 0c-.55 0-1 .45-1 1s.45 1 1 1zm18 0l2 0c.55 0 1-.45 1-1s-.45-1-1-1l-2 0c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37c-.39-.39-1.03-.39-1.41 0-.39.39-.39 1.03 0 1.41l1.06 1.06c.39.39 1.03.39 1.41 0 .39-.39.39-1.03 0-1.41l-1.06-1.06zm1.06-10.96c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36c.39-.39.39-1.03 0-1.41-.39-.39-1.03-.39-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z";
    const string MoonGeometry = "M12 3c-4.97 0-9 4.03-9 9s4.03 9 9 9 9-4.03 9-9c0-.46-.04-.92-.1-1.36-.98 1.37-2.58 2.26-4.4 2.26-2.98 0-5.4-2.42-5.4-5.4 0-1.81.89-3.42 2.26-4.4-.44-.06-.9-.1-1.36-.1z";
    const string AutoGeometry = "M12 22c5.52 0 10-4.48 10-10S17.52 2 12 2 2 6.48 2 12s4.48 10 10 10zm1-17.93c3.94.49 7 3.85 7 7.93s-3.05 7.44-7 7.93V4.07z";

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
        var (name, family) = FontSteps[_fontIndex];
        FontFamily = new FontFamily(family);
        ToolTip.SetTip(FontButton, $"UI font: {name}");
    }

    void ApplyZoom()
    {
        var (name, scale) = ZoomSteps[_zoomIndex];
        var resources = Avalonia.Application.Current!.Resources;
        foreach (var (key, baseValue) in ZoomResources)
            resources[key] = baseValue * scale;
        ToolTip.SetTip(ZoomButton, $"Zoom: {name}");
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
        // Light/dark switch icon: one consistent Material family (sun / moon / contrast).
        ThemePath.Data = Avalonia.Media.Geometry.Parse(t.Name switch
        {
            "light" => SunGeometry,
            "graphite" => MoonGeometry,
            _ => AutoGeometry,
        });
        ThemePath.Fill = new SolidColorBrush(EffectiveVariant() == ThemeVariant.Light
            ? Color.Parse("#1B2420") : Color.Parse("#E4E9E6"));
        ToolTip.SetTip(ThemeButton, t.Name == "auto" ? "Theme: follow system" : $"Theme: {t.Name}");
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
        ToolTip.SetTip(AccentButton, $"Accent: {name}");

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

    void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        _themeIndex = (_themeIndex + 1) % Palettes.Length;
        ApplyTheme();
        Persist(p => p.Theme = Palettes[_themeIndex].Name);
    }

    void OnAccentClick(object? sender, RoutedEventArgs e)
    {
        _accentIndex = (_accentIndex + 1) % AccentSteps.Length;
        ApplyAccent();
        Persist(p => p.Accent = AccentSteps[_accentIndex].Name);
    }

    void OnFontClick(object? sender, RoutedEventArgs e)
    {
        _fontIndex = (_fontIndex + 1) % FontSteps.Length;
        ApplyFont();
        Persist(p => p.Font = FontSteps[_fontIndex].Name);
    }

    void OnZoomClick(object? sender, RoutedEventArgs e)
    {
        _zoomIndex = (_zoomIndex + 1) % ZoomSteps.Length;
        ApplyZoom();
        Persist(p => p.Zoom = ZoomSteps[_zoomIndex].Name);
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
