using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class MainWindow : Window, IDialogs
{
    static readonly (string Name, ThemeVariant Variant, string? Background)[] ThemeSteps =
    {
        ("auto", ThemeVariant.Default, null),
        ("light", ThemeVariant.Light, "#FFFFFF"),
        ("pearl", ThemeVariant.Light, "#F1EFEA"),
        ("ash", ThemeVariant.Light, "#DEDCD7"),
        ("steel", ThemeVariant.Dark, "#4A5158"),
        ("slate", ThemeVariant.Dark, "#383E45"),
        ("graphite", ThemeVariant.Dark, "#26292D"),
        ("dark", ThemeVariant.Dark, null),
    };
    static readonly (string Name, double Opacity)[] ContrastSteps =
    {
        ("soft", 0.5),
        ("normal", 0.68),
        ("high", 0.9),
    };
    static readonly (string Name, string Hex)[] AccentSteps =
    {
        ("blue", "#3378DD"),
        ("teal", "#1D9E75"),
        ("purple", "#7A5BD0"),
        ("amber", "#C77E16"),
        ("rose", "#C94F6D"),
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
        ("CtrlH", 26), ("HeaderH", 30), ("CollapseH", 28),
    };

    int _themeIndex;
    int _contrastIndex = 1;
    int _accentIndex;
    int _fontIndex;
    int _zoomIndex;

    public MainWindow()
    {
        InitializeComponent();
        // The header lives inside the extended title bar area — make it draggable.
        HeaderBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                BeginMoveDrag(e);
        };
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
        _contrastIndex = Math.Max(0, Array.FindIndex(ContrastSteps, s => s.Name == prefs.Contrast));
        _accentIndex = Math.Max(0, Array.FindIndex(AccentSteps, s => s.Name == prefs.Accent));
        _fontIndex = Math.Max(0, Array.FindIndex(FontSteps, s => s.Name == prefs.Font));
        _zoomIndex = Math.Max(0, Array.FindIndex(ZoomSteps, s => s.Name == prefs.Zoom));
        ApplyTheme();
        ApplyContrast();
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
        var (name, variant, background) = ThemeSteps[_themeIndex];
        Avalonia.Application.Current!.RequestedThemeVariant = variant;
        if (background is null) ClearValue(BackgroundProperty);
        else Background = new SolidColorBrush(Color.Parse(background));
        ThemeLabel.Text = name;
    }

    void ApplyContrast()
    {
        var (name, opacity) = ContrastSteps[_contrastIndex];
        var resources = Avalonia.Application.Current!.Resources;
        resources["DimOpacity"] = opacity;
        // Borders follow contrast too: hairlines and field borders get stronger with it.
        var (hairline, field) = name switch
        {
            "soft" => ((byte)0x20, (byte)0x2C),
            "high" => ((byte)0x58, (byte)0x6E),
            _ => ((byte)0x33, (byte)0x40),
        };
        resources["HairlineBrush"] = new SolidColorBrush(Color.FromArgb(hairline, 0x80, 0x80, 0x80));
        resources["FieldBorderBrush"] = new SolidColorBrush(Color.FromArgb(field, 0x80, 0x80, 0x80));
        ContrastLabel.Text = name;
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

    void OnContrastClick(object? sender, RoutedEventArgs e)
    {
        _contrastIndex = (_contrastIndex + 1) % ContrastSteps.Length;
        ApplyContrast();
        Persist(p => p.Contrast = ContrastSteps[_contrastIndex].Name);
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
