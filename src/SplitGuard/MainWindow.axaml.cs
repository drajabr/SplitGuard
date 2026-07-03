using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class MainWindow : Window, IDialogs
{
    // Each theme is one coherent palette: page + card surface + chip fill + border
    // strength + secondary-text contrast, tuned together. null page/surface/item = the
    // OS-adaptive overlay path ("auto"). This folds the old separate "contrast" control in.
    record ThemeDef(string Name, ThemeVariant Variant, string? Page, string? Surface, string? Item,
                 double Dim, byte Hair, byte Field);

    static readonly ThemeDef[] ThemeSteps =
    {
        new("auto",     ThemeVariant.Default, null,      null,      null,      0.68, 0x33, 0x40),
        new("light",    ThemeVariant.Light,  "#F4F3F0", "#FFFFFF", "#ECEAE4", 0.62, 0x33, 0x42),
        new("pearl",    ThemeVariant.Light,  "#EAE8E2", "#F7F5F0", "#E0DDD5", 0.62, 0x36, 0x46),
        new("ash",      ThemeVariant.Light,  "#DBD9D2", "#EAE8E2", "#D0CDC5", 0.60, 0x3C, 0x4C),
        new("steel",    ThemeVariant.Dark,   "#464C53", "#525860", "#3C424A", 0.72, 0x44, 0x56),
        new("slate",    ThemeVariant.Dark,   "#363C43", "#414750", "#2D333A", 0.72, 0x3E, 0x50),
        new("graphite", ThemeVariant.Dark,   "#24272B", "#2E3339", "#1F2226", 0.70, 0x38, 0x48),
        new("dark",     ThemeVariant.Dark,   "#1A1C1F", "#25282C", "#17191C", 0.70, 0x34, 0x44),
    };
    static readonly (string Name, string Hex)[] AccentSteps =
    {
        ("blue", "#3378DD"),
        ("indigo", "#5566D8"),
        ("purple", "#7A5BD0"),
        ("magenta", "#A94BC0"),
        ("rose", "#C94F6D"),
        ("red", "#CE4038"),
        ("orange", "#C56A1C"),
        ("amber", "#C08A12"),
        ("green", "#4F9A34"),
        ("teal", "#1D9E75"),
        ("cyan", "#1394A8"),
    };
    static readonly (string Name, string Family)[] FontSteps =
    {
        ("segoe", "Segoe UI Variable Text, Segoe UI"),
        ("bahnschrift", "Bahnschrift, Segoe UI"),
        ("verdana", "Verdana, Segoe UI"),
        ("trebuchet", "Trebuchet MS, Segoe UI"),
        ("candara", "Candara, Segoe UI"),
        ("georgia", "Georgia, Cambria, Times New Roman"),
        ("mono", "Cascadia Mono, Consolas"),
    };
    static readonly (string Name, double Scale)[] ZoomSteps =
    {
        ("100%", 1.0),
        ("110%", 1.1),
        ("120%", 1.2),
        ("130%", 1.3),
        ("140%", 1.4),
    };
    // Fonts and layout metrics scale together — no transforms, so wrapping stays correct.
    static readonly (string Key, double Base)[] ZoomResources =
    {
        ("Fs9", 9), ("Fs11", 11), ("Fs115", 11.5), ("Fs12", 12), ("Fs125", 12.5),
        ("Fs13", 13), ("Fs135", 13.5), ("Fs14", 14), ("Fs17", 17),
        ("CtrlH", 26), ("HeaderH", 30), ("CollapseH", 96),
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
        _themeIndex = Math.Max(0, Array.FindIndex(ThemeSteps, s => s.Name == prefs.Theme));
        _accentIndex = Math.Max(0, Array.FindIndex(AccentSteps, s => s.Name == prefs.Accent));
        _fontIndex = Math.Max(0, Array.FindIndex(FontSteps, s => s.Name == prefs.Font));
        _zoomIndex = Math.Max(0, Array.FindIndex(ZoomSteps, s => s.Name == prefs.Zoom));
        ApplyTheme();
        ApplyAccent();
        ApplyFont();
        ApplyZoom();
    }

    void ApplyFont()
    {
        var (name, family) = FontSteps[_fontIndex];
        FontFamily = new FontFamily(family);
        FontLabel.Text = name;
    }

    void ApplyZoom()
    {
        var (name, scale) = ZoomSteps[_zoomIndex];
        var resources = Avalonia.Application.Current!.Resources;
        foreach (var (key, baseValue) in ZoomResources)
            resources[key] = baseValue * scale;
        ZoomLabel.Text = name;
    }

    void ApplyTheme()
    {
        var t = ThemeSteps[_themeIndex];
        var resources = Avalonia.Application.Current!.Resources;
        Avalonia.Application.Current!.RequestedThemeVariant = t.Variant;

        if (t.Page is null) ClearValue(BackgroundProperty);
        else Background = new SolidColorBrush(Color.Parse(t.Page));
        // Opaque surfaces when the theme defines a palette; transparent overlays under "auto".
        resources["SurfaceBrush"] = new SolidColorBrush(t.Surface is null ? Color.Parse("#00FFFFFF") : Color.Parse(t.Surface));
        resources["ItemBrush"] = new SolidColorBrush(t.Item is null ? Color.FromArgb(0x14, 0x80, 0x80, 0x80) : Color.Parse(t.Item));
        resources["DimOpacity"] = t.Dim;
        resources["HairlineBrush"] = new SolidColorBrush(Color.FromArgb(t.Hair, 0x80, 0x80, 0x80));
        resources["FieldBorderBrush"] = new SolidColorBrush(Color.FromArgb(t.Field, 0x80, 0x80, 0x80));
        ThemeLabel.Text = t.Name;
    }

    void ApplyAccent()
    {
        var (name, hex) = AccentSteps[_accentIndex];
        var color = Color.Parse(hex);
        var resources = Avalonia.Application.Current!.Resources;
        resources["AccentBrush"] = new SolidColorBrush(color);
        resources["AccentDimBrush"] = new SolidColorBrush(color, 0.4);
        // Fluent's own accent: recolors toggle switches, focus rings, selection, etc.
        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorDark1"] = Shade(color, 0.85);
        resources["SystemAccentColorDark2"] = Shade(color, 0.70);
        resources["SystemAccentColorDark3"] = Shade(color, 0.55);
        resources["SystemAccentColorLight1"] = Tint(color, 0.15);
        resources["SystemAccentColorLight2"] = Tint(color, 0.30);
        resources["SystemAccentColorLight3"] = Tint(color, 0.45);

        // Syntax palette leans slightly toward the accent, in fields and the raw editor alike.
        // Icon set (window, tray, header logo) recomposed in the accent color.
        var icons = AppIcons.Get(color);
        Icon = icons.Idle;
        LogoImage.Source = icons.Logo;
        if (Avalonia.Application.Current is App app)
            app.SetAccentIcons(icons.Idle, icons.Active);

        var ip = Mix(Color.Parse("#4098D7"), color, 0.22);
        var domain = Mix(Color.Parse("#58A65C"), color, 0.22);
        var key = Mix(Color.Parse("#9A6FD0"), color, 0.22);
        var num = Mix(Color.Parse("#C77E16"), color, 0.22);
        resources["SynIpBrush"] = new SolidColorBrush(ip);
        resources["SynDomainBrush"] = new SolidColorBrush(domain);
        resources["SynKeyBrush"] = new SolidColorBrush(key);
        resources["SynNumBrush"] = new SolidColorBrush(num);
        TunnelCard.UpdateHighlighting(Hex(color), Hex(num), Hex(key), Hex(ip), Hex(num), Hex(domain));
        AccentLabel.Text = name;
    }

    static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

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

    void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        _themeIndex = (_themeIndex + 1) % ThemeSteps.Length;
        ApplyTheme();
        Persist(p => p.Theme = ThemeSteps[_themeIndex].Name);
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

    public async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }
}
