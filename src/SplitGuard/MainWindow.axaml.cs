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
    // Four explicit shades from brightest to darkest, plus "auto" (follows the OS light/dark).
    // white = crisp pure white (cards read via their border); light = a softer, dimmer off-white
    // with cards lifted above the page; graphite = the muted dark; black = true black (OLED).
    static readonly ThemeDef[] Palettes =
    {
        new("auto",     ThemeVariant.Default, null,      null,      null,      0.68, 0x45, 0x40),
        new("white",    ThemeVariant.Light,  "#FFFFFF", "#FFFFFF", "#EEECE6", 0.60, 0x42, 0x3E),
        new("light",    ThemeVariant.Light,  "#DEDBD2", "#ECE9E2", "#D3D0C6", 0.62, 0x48, 0x42),
        new("graphite", ThemeVariant.Dark,   "#24272B", "#2E3339", "#1F2226", 0.70, 0x4A, 0x48),
        new("black",    ThemeVariant.Dark,   "#000000", "#141619", "#0C0D0F", 0.72, 0x56, 0x54),
    };

    // Accent hue is its own control, independent of the surface theme (see Views.Accents).
    static (string Name, string Hex)[] AccentSteps => Accents.Steps;

    // Distinct UI fonts, applied to the whole window (including values/fields).
    static readonly (string Name, string Family)[] FontSteps =
    {
        ("segoe",       "Segoe UI Variable Text, Segoe UI"),           // modern sans (default)
        ("bahnschrift", "Bahnschrift, Segoe UI"),                      // condensed industrial
        ("georgia",     "Georgia, Cambria, serif"),                    // serif
        ("candara",     "Candara, Segoe UI"),                          // humanist sans
        ("mono",        "Cascadia Mono, Consolas, Courier New, monospace"), // monospace
    };
    static readonly (string Name, double Scale)[] ZoomSteps =
    {
        ("1x",   1.0),
        ("1.1x", 1.1),
        ("1.2x", 1.2),
        ("1.3x", 1.3),
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
        // "Drop to import" overlay while files hover anywhere over the window.
        AddHandler(DragDrop.DragEnterEvent, (_, e) =>
            DropOverlay.IsVisible = e.Data.Contains(DataFormats.Files));
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Collapse the settings panel on any press outside it (tunnelled + handledEventsToo so it
        // still fires when a card or control handles the press first).
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Under the "auto" theme, an OS light<->dark flip must re-resolve every variant-dependent
        // resource (field fills, menu foregrounds, the mono accent). Re-running ApplyTheme sets the
        // same RequestedThemeVariant, so this can't loop — the event only fires on a real change.
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        // The floating cluster yields to content: while cards extend behind it, it fades out so
        // nothing is obscured, and fades back when the pointer enters its area (or the overlap
        // clears — short list, scrolled to the end, drawer closed).
        MainScroll.ScrollChanged += (_, _) => UpdateClusterFade();
        BottomCluster.PointerEntered += (_, _) => { _clusterHover = true; UpdateClusterFade(); };
        BottomCluster.PointerExited += (_, _) => { _clusterHover = false; UpdateClusterFade(); };
        LayoutUpdated += (_, _) => UpdateClusterFade();
    }

    bool _clusterHover;
    const double ListBottomPad = 70; // the scroll content's reserved bottom margin (XAML)

    void UpdateClusterFade()
    {
        // An expanded drawer is something the user explicitly opened — it never auto-hides.
        // Only the bare bar yields to content scrolling behind it.
        if (_openDrawer != Drawer.None) { BottomCluster.Opacity = 1; return; }
        // Where the cards actually end, in ListHost coordinates: the reserved bottom margin is
        // part of the scroll extent but holds no content, so subtract it back out.
        var cardsBottom = MainScroll.Extent.Height - ListBottomPad - MainScroll.Offset.Y;
        var clusterTop = ListHost.Bounds.Height - BottomCluster.Bounds.Height;
        var overlap = cardsBottom > clusterTop + 1;
        BottomCluster.Opacity = !overlap || _clusterHover ? 1 : 0;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
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
        // Esc peels back one layer at a time: drop field focus, then close an open drawer,
        // then collapse (cancel) the editing card.
        if (e.Key == Key.Escape && !e.Handled)
        {
            if (FocusManager?.GetFocusedElement() is TextBox)
            {
                MainScroll.Focus();
                e.Handled = true;
                return;
            }
            if (_openDrawer != Drawer.None)
            {
                SetDrawer(Drawer.None);
                e.Handled = true;
                return;
            }
            var editing = vm?.Tunnels.FirstOrDefault(t => t.IsEditing);
            if (editing is not null)
            {
                editing.CancelEditCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        if (ctrl && e.Key == Key.N && vm is not null)
        {
            vm.CreateEmptyTunnel();
            e.Handled = true;
            return;
        }
        // Ctrl+O opens the file picker to import a .conf (mirrors the Add drawer's Import row).
        // Not while typing in a field (same guard as Ctrl+V), and close any open drawer first so
        // it isn't left hanging under the file dialog.
        if (ctrl && e.Key == Key.O && !e.Handled && vm is not null
            && FocusManager?.GetFocusedElement() is not TextBox)
        {
            SetDrawer(Drawer.None);
            _ = ImportConfAsync();
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

    // Saved zoom names from before the 1x..1.3x rename; without this map an upgrading user's
    // "125%"/"150%" wouldn't match and would silently reset to 1x.
    static string MigrateZoomName(string? name) => name switch
    {
        "100%" => "1x",
        "125%" => "1.2x", // nearest new step
        "150%" => "1.3x",
        _ => name ?? "1x",
    };

    // Restore the last window size/position when the saved rect still lands on a visible
    // screen (a monitor may have been unplugged since); otherwise keep the defaults.
    public void RestoreWindowBounds(SplitGuard.Models.UiPrefs p)
    {
        if (p.WindowW < (int)MinWidth || p.WindowH < (int)MinHeight) return; // never saved (or nonsense)
        Width = p.WindowW;
        Height = p.WindowH;
        var pos = new PixelPoint(p.WindowX, p.WindowY);
        foreach (var s in Screens.All)
        {
            // The title bar's midpoint must be reachable so the window can always be dragged.
            if (!s.WorkingArea.Contains(new PixelPoint(pos.X + p.WindowW / 2, pos.Y + 20))) continue;
            Position = pos;
            return;
        }
    }

    // Capture the current bounds for persistence — only a Normal window's geometry is worth
    // keeping (a maximized/minimized rect would restore wrong).
    public void SaveWindowBounds(SplitGuard.Models.UiPrefs p)
    {
        if (WindowState != WindowState.Normal) return;
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        p.WindowW = (int)Bounds.Width;
        p.WindowH = (int)Bounds.Height;
        p.WindowX = Position.X;
        p.WindowY = Position.Y;
    }

    public void ApplyUiPrefs(SplitGuard.Models.UiPrefs prefs)
    {
        prefs.Zoom = MigrateZoomName(prefs.Zoom);
        _themeIndex = Math.Max(0, Array.FindIndex(Palettes, p => p.Name == prefs.Theme));
        _accentIndex = Math.Max(0, Array.FindIndex(AccentSteps, a => a.Name == prefs.Accent));
        _fontIndex = Math.Max(0, Array.FindIndex(FontSteps, s => s.Name == prefs.Font));
        _zoomIndex = Math.Max(0, Array.FindIndex(ZoomSteps, s => s.Name == prefs.Zoom));
        ApplyTheme();   // also applies the accent (keeps "mono" in sync with the theme)
        ApplyFont();
        ApplyZoom();
        BuildSettingsPanel();
        // Reconcile the run-at-boot registry entry with the (possibly default-on) pref — never
        // from the UI-review harness, which must not point the Run key at a dev binary.
        if (!Services.RuleStore.DemoMode)
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
        // "auto" is not its own palette — it simply IS the white palette when the OS is light and
        // the black palette when the OS is dark. Resolve to that concrete palette here, keeping
        // Variant=Default so RequestedThemeVariant follows the OS and ActualThemeVariantChanged
        // keeps re-running this on a light/dark flip.
        if (t.Variant == ThemeVariant.Default)
        {
            var osLight = ActualThemeVariant == ThemeVariant.Light;
            t = Palettes.First(p => p.Name == (osLight ? "white" : "black")) with { Variant = ThemeVariant.Default };
        }
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
        // Soft neutral fill behind borderless fields; a touch stronger on hover. Keyed on the
        // EFFECTIVE variant (not the palette name) so "white" and a light-OS "auto" get the
        // lighter wash too, not just the palette literally named "light".
        var lightFill = EffectiveVariant() == ThemeVariant.Light;
        resources["FieldFillBrush"] = new SolidColorBrush(Color.FromArgb(lightFill ? (byte)0x14 : (byte)0x1E, 0x80, 0x80, 0x80));
        resources["FieldFillHoverBrush"] = new SolidColorBrush(Color.FromArgb(lightFill ? (byte)0x20 : (byte)0x2E, 0x80, 0x80, 0x80));
        // Syntax palette (IPs, domains, keys, numbers) as a THEME-AWARE global: the fixed mid-tones
        // washed out on the bright themes, so light themes get darker, more saturated variants and
        // dark themes the brighter ones. Consumed by the collapsed detail and the edit-field chips.
        resources["SynIpBrush"]     = new SolidColorBrush(Color.Parse(lightFill ? "#1F6FB0" : "#57A9E0"));
        resources["SynDomainBrush"] = new SolidColorBrush(Color.Parse(lightFill ? "#2E7D32" : "#6FC06F"));
        resources["SynKeyBrush"]    = new SolidColorBrush(Color.Parse(lightFill ? "#6A3FA0" : "#B08BE0"));
        resources["SynNumBrush"]    = new SolidColorBrush(Color.Parse(lightFill ? "#B26A00" : "#E0A040"));
        // One elevation shadow shared by every floating surface — the bottom bar, the Settings/Add
        // drawers, and the tunnel + peer cards — so they all read as the same layer. Dark themes
        // need a heavier shadow to register (a card sits on the darkest element, the page, so a
        // faint one is invisible dark-on-dark); light themes a soft grey one. Kept short: the blur
        // reach must stay under the cards' 14px side inset, or the scroll viewport clips the
        // overflow into a hard vertical edge.
        var shadow = BoxShadows.Parse(lightFill ? "0 2 7 0 #38000000" : "0 2 10 0 #A6000000");
        resources["FloatShadow"] = shadow;
        resources["CardShadow"] = shadow;
        // Menus/popups need an opaque backing (cards may be translucent overlays under "auto").
        var menuBg = t.Surface is not null
            ? Color.Parse(t.Surface)
            : (EffectiveVariant() == ThemeVariant.Light ? Color.Parse("#FBFBFB") : Color.Parse("#2B2F34"));
        resources["MenuSurfaceBrush"] = new SolidColorBrush(menuBg);
        // Keys consumed by the Fluent MenuFlyoutPresenter/MenuItem (the in-app menu bar is gone;
        // these still back the TextBox context flyout). The hover shade is set in ApplyAccent.
        resources["MenuFlyoutPresenterBackground"] = new SolidColorBrush(menuBg);
        resources["MenuFlyoutPresenterBorderBrush"] = resources["HairlineBrush"];
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
        // Menu item hover/press = a soft shade of the accent (menu bar + tray menu share the
        // Fluent MenuItem theme, so both pick these up).
        resources["MenuFlyoutItemBackgroundPointerOver"] = new SolidColorBrush(color, 0.20);
        resources["MenuFlyoutItemBackgroundPressed"] = new SolidColorBrush(color, 0.30);
        resources["MenuFlyoutItemBackgroundSelected"] = new SolidColorBrush(color, 0.20);
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

    // ---- header + settings panel ----------------------------------------------

    // Header update affordance: install when ready, otherwise (re)check — the VM steps the state.
    void OnUpdateClick(object? sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.OnUpdateButtonClicked();

    // Clicking a toast dismisses it immediately (it also auto-clears after a few seconds).
    void OnToastPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.StatusText = "";
        e.Handled = true;
    }

    // Bottom-bar Add actions. Each closes the open drawer first, then runs.
    void OnImportClick(object? sender, RoutedEventArgs e) { SetDrawer(Drawer.None); _ = ImportConfAsync(); }
    void OnNewTunnelClick(object? sender, RoutedEventArgs e) { SetDrawer(Drawer.None); (DataContext as MainViewModel)?.CreateEmptyTunnel(); }
    void OnRescanClick(object? sender, RoutedEventArgs e) { SetDrawer(Drawer.None); (DataContext as MainViewModel)?.RescanExternals(); }

    // ---- bottom-bar drawers: Settings and Add each grow their own card upward over the list;
    // only one is open at a time, and any outside press closes it. --------------------------------

    enum Drawer { None, Settings, Add }
    Drawer _openDrawer = Drawer.None;
    int _setGen, _addGen; // per-region guards cancelling a stale expand/collapse finalize

    void OnSettingsToggleClick(object? sender, RoutedEventArgs e) =>
        SetDrawer(_openDrawer == Drawer.Settings ? Drawer.None : Drawer.Settings);
    void OnAddToggleClick(object? sender, RoutedEventArgs e) =>
        SetDrawer(_openDrawer == Drawer.Add ? Drawer.None : Drawer.Add);

    // Collapse the open drawer when a press lands outside both drawers and both toggles (a tunnel
    // card, the list, the title bar). Tunnelled + handledEventsToo so it fires even when a child
    // handles the press first.
    void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_openDrawer == Drawer.None) return;
        for (var el = e.Source as Visual; el is not null; el = el.GetVisualParent())
            if (ReferenceEquals(el, SettingsRegion) || ReferenceEquals(el, SettingsToggle)
                || ReferenceEquals(el, AddRegion) || ReferenceEquals(el, AddToggle)) return;
        SetDrawer(Drawer.None);
    }

    // Drive both regions to match the requested drawer (the other collapses). Settings rebuilds on
    // open so its state is fresh (e.g. after a tray toggle); the Add card is static.
    void SetDrawer(Drawer which)
    {
        _openDrawer = which;
        if (which == Drawer.Settings) BuildSettingsPanel();
        // ".open" wears the floating shadow — a closed (zero-height) region must paint nothing.
        SettingsRegion.Classes.Set("open", which == Drawer.Settings);
        AddRegion.Classes.Set("open", which == Drawer.Add);
        AnimateRegion(SettingsRegion, SettingsCard, SettingsChevron, which == Drawer.Settings, ++_setGen, () => _setGen);
        AnimateRegion(AddRegion, AddCard, AddChevron, which == Drawer.Add, ++_addGen, () => _addGen);
    }

    // Tween a region's Height between 0 and its card's natural height (shared Slow token), then
    // release to Auto when open so it follows content. gen/cur guard against rapid re-toggle.
    void AnimateRegion(Border region, Control card, TextBlock chevron, bool open, int gen, Func<int> cur)
    {
        chevron.Text = open ? "" : ""; // down = click to close; up = opens upward
        var from = region.Bounds.Height;
        double to = 0;
        if (open)
        {
            var w = region.Bounds.Width;
            if (w < 1) w = Bounds.Width;
            card.Measure(new Size(w, double.PositiveInfinity));
            // The region is the rounded card; its natural height is the inner content (whose
            // Margin is the card's inset, included in DesiredSize) plus the region's border.
            to = card.DesiredSize.Height
                 + region.Padding.Top + region.Padding.Bottom
                 + region.BorderThickness.Top + region.BorderThickness.Bottom;
        }
        // The vertical margins ride the height animation (closed = zero footprint, open = 8px
        // gaps), so the open drawer always hugs the bar and a closed one leaves no dead space.
        var m0 = region.Margin.Top;
        double m1 = open ? 8 : 0;
        TunnelCard.Tween(from, to, Motion.SlowMs,
            v =>
            {
                if (cur() != gen) return;
                region.Height = v;
                var f = Math.Abs(to - from) < 0.5 ? 1 : Math.Clamp((v - from) / (to - from), 0, 1);
                var m = m0 + (m1 - m0) * f;
                region.Margin = new Thickness(14, m, 14, m);
            },
            () =>
            {
                if (cur() != gen) return;
                if (open) region.Height = double.NaN;
                region.Margin = new Thickness(14, m1, 14, m1);
            });
    }

    // (Re)build the two columns. General = toggles with side effects; Appearance = inline pickers.
    void BuildSettingsPanel()
    {
        if (DataContext is not MainViewModel sv) return;
        GeneralList.Children.Clear();
        GeneralList.Children.Add(ToggleRow("Custom DNS forwarding", () => sv.HasCustomDns, on => sv.ToggleCustomDns(on)));
        // The registry/scheduled-task side effects are skipped in the UI-review harness — a demo
        // session toggling a switch must never install the dev binary as a logon task (or delete
        // the installed app's launch task). The pref writes are already no-ops there (Save gates).
        GeneralList.Children.Add(ToggleRow("Start on Windows startup", () => sv.Prefs.StartOnBoot, on =>
        {
            if (!Services.RuleStore.DemoMode) StartupService.Set(on);
            sv.Prefs.StartOnBoot = on; sv.PersistPrefs();
        }));
        GeneralList.Children.Add(ToggleRow("Skip UAC prompt on launch", () => sv.Prefs.SkipUacLaunch, on =>
        {
            sv.Prefs.SkipUacLaunch = on; sv.PersistPrefs();
            if (!Services.RuleStore.DemoMode)
                _ = Task.Run(() =>
                {
                    if (on) StartupService.RegisterLaunchTask();
                    else StartupService.UnregisterLaunchTask();
                });
        }));
        GeneralList.Children.Add(ToggleRow("Notifications", () => sv.Prefs.Notifications, on => { sv.Prefs.Notifications = on; sv.PersistPrefs(); }));
        // Turning on the update check runs one now, so a found update surfaces the header arrow.
        GeneralList.Children.Add(ToggleRow("Check for updates on startup", () => sv.Prefs.CheckUpdates, on =>
        {
            sv.Prefs.CheckUpdates = on; sv.PersistPrefs();
            if (on) _ = sv.CheckForUpdatesAsync(manual: true);
        }));

        AppearanceList.Children.Clear();
        AppearanceList.Children.Add(ThemeGroup());
        AppearanceList.Children.Add(AccentGroup());
        AppearanceList.Children.Add(FontGroup());
        AppearanceList.Children.Add(PickerGroup(System.Array.ConvertAll(ZoomSteps, s => s.Name), _zoomIndex, SelectZoom));
    }

    // "label [ switch ]" row: a pill switch (shared look with the connect toggle) flushed right.
    // Uses Click (fires after the toggle) so building the row with the current state never re-runs
    // the side effect.
    Control ToggleRow(string label, Func<bool> get, Action<bool> set)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3, 0, 3) };
        var tb = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        tb.Classes.Add("setlabel");
        Grid.SetColumn(tb, 0);
        var sw = new ToggleButton { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        sw.Classes.Add("pill");
        sw.Click += (_, _) => set(sw.IsChecked == true);
        Grid.SetColumn(sw, 1);
        grid.Children.Add(tb);
        grid.Children.Add(sw);
        return grid;
    }

    // Equal-width segmented option row; the current one wears the accent fill (Button.seg.sel).
    // No label — the options are self-evident and it keeps the panel compact. Buttons split the
    // full width evenly; a symmetric 2.5px margin on each button and on the row gives one uniform
    // 5px gap between every button and at both borders.
    Control PickerGroup(string[] names, int current, Action<int> pick)
    {
        var row = new UniformGrid { Rows = 1, Columns = names.Length, Margin = new Thickness(2.5, 0) };
        var buttons = new List<Button>();
        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = names[i], Height = 26, Margin = new Thickness(2.5, 0) };
            b.Classes.Add("seg");
            if (i == current) b.Classes.Add("sel");
            b.Click += (_, _) =>
            {
                pick(idx);
                for (int k = 0; k < buttons.Count; k++) buttons[k].Classes.Set("sel", k == idx);
            };
            row.Children.Add(b);
            buttons.Add(b);
        }
        return row;
    }

    // Font picker: an "Ag" sample rendered in each typeface (reads as the font itself and never
    // truncates); the family name is a tooltip.
    Control FontGroup()
    {
        var row = new UniformGrid { Rows = 1, Columns = FontSteps.Length, Margin = new Thickness(2.5, 0) };
        var buttons = new List<Button>();
        for (int i = 0; i < FontSteps.Length; i++)
        {
            int idx = i;
            var (name, family) = FontSteps[i];
            var sample = new TextBlock { Text = "Ag", FontFamily = new FontFamily(family), FontSize = 14 };
            var b = new Button { Content = sample, Height = 26, Margin = new Thickness(2.5, 0) };
            b.Classes.Add("seg");
            ToolTip.SetTip(b, name);
            if (i == _fontIndex) b.Classes.Add("sel");
            b.Click += (_, _) =>
            {
                SelectFont(idx);
                for (int k = 0; k < buttons.Count; k++) buttons[k].Classes.Set("sel", k == idx);
            };
            row.Children.Add(b);
            buttons.Add(b);
        }
        return row;
    }

    // Theme picker: each option is a full box filled with the palette's page shade, so the row
    // reads as a light->dark ramp; "auto" is a split light/dark box signalling "follows the OS". A
    // hairline keeps the near-white boxes visible on a white card. Selected box = accent ring.
    Control ThemeGroup()
    {
        var hair = this.FindResource("HairlineBrush") as IBrush ?? Brushes.Gray;
        var row = new UniformGrid { Rows = 1, Columns = Palettes.Length, Margin = new Thickness(2.5, 0) };
        var buttons = new List<Button>();
        for (int i = 0; i < Palettes.Length; i++)
        {
            int idx = i;
            var pal = Palettes[i];
            var box = new Border { CornerRadius = new CornerRadius(4), BorderBrush = hair, BorderThickness = new Thickness(1) };
            if (pal.Page is null)
                box.Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#F7F7F5"), 0.5), new GradientStop(Color.Parse("#26292E"), 0.5) },
                };
            else
                box.Background = new SolidColorBrush(Color.Parse(pal.Page));
            var b = new Button { Content = box, Height = 26, Margin = new Thickness(2.5, 0) };
            b.Classes.Add("swatch");
            ToolTip.SetTip(b, pal.Name);
            if (i == _themeIndex) b.Classes.Add("sel");
            b.Click += (_, _) =>
            {
                SelectTheme(idx);
                for (int k = 0; k < buttons.Count; k++) buttons[k].Classes.Set("sel", k == idx);
            };
            row.Children.Add(b);
            buttons.Add(b);
        }
        return row;
    }

    // Accent picker: each option is a full box filled with the hue; the selected box gets an
    // accent ring (Button.swatch).
    Control AccentGroup()
    {
        var row = new UniformGrid { Rows = 1, Columns = AccentSteps.Length, Margin = new Thickness(2.5, 0) };
        var buttons = new List<Button>();
        for (int i = 0; i < AccentSteps.Length; i++)
        {
            int idx = i;
            var (_, hex) = AccentSteps[i];
            var box = new Border { CornerRadius = new CornerRadius(4) };
            if (hex.Length > 0)
                box.Background = new SolidColorBrush(Color.Parse(hex));
            else // "mono" tracks the theme's black/white — show it as a split black/white box
                box.Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#FFFFFF"), 0.5), new GradientStop(Color.Parse("#111111"), 0.5) },
                };
            var b = new Button { Content = box, Height = 26, Margin = new Thickness(2.5, 0) };
            b.Classes.Add("swatch");
            if (i == _accentIndex) b.Classes.Add("sel");
            b.Click += (_, _) =>
            {
                SelectAccent(idx);
                for (int k = 0; k < buttons.Count; k++) buttons[k].Classes.Set("sel", k == idx);
            };
            row.Children.Add(b);
            buttons.Add(b);
        }
        return row;
    }

    void SelectTheme(int i) { _themeIndex = i; ApplyTheme(); Persist(p => p.Theme = Palettes[i].Name); }
    void SelectAccent(int i) { _accentIndex = i; ApplyAccent(); Persist(p => p.Accent = AccentSteps[i].Name); }
    void SelectFont(int i) { _fontIndex = i; ApplyFont(); Persist(p => p.Font = FontSteps[i].Name); }
    void SelectZoom(int i) { _zoomIndex = i; ApplyZoom(); Persist(p => p.Zoom = ZoomSteps[i].Name); }

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
