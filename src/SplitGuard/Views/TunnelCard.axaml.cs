using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class TunnelCard : UserControl
{
    // wg-quick highlighting using the one fixed syntax palette (Syntax.*), so the
    // editor matches the field/description colors and never shifts with the accent.
    static readonly IHighlightingDefinition ConfHighlighting = LoadHighlighting($$"""
        <SyntaxDefinition name="WgConf" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="Section" foreground="{{Syntax.Prop}}" fontWeight="bold" />
          <Color name="Prop" foreground="{{Syntax.Prop}}" />
          <Color name="Key64" foreground="{{Syntax.Key}}" />
          <Color name="Ip" foreground="{{Syntax.Ip}}" />
          <Color name="Num" foreground="{{Syntax.Num}}" />
          <Color name="Domain" foreground="{{Syntax.Domain}}" />
          <RuleSet>
            <Span color="Comment" begin="#" />
            <Rule color="Section">\[[^\]]*\]</Rule>
            <Rule color="Prop">^\s*[A-Za-z]+(?=\s*=)</Rule>
            <Rule color="Key64">[A-Za-z0-9+/]{42,44}=</Rule>
            <Rule color="Ip">\b\d{1,3}(\.\d{1,3}){3}\b</Rule>
            <Rule color="Num">[/:]\d+\b</Rule>
            <Rule color="Num">\b\d+\b</Rule>
            <Rule color="Domain">\b(\*\.)?[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """);

    static IHighlightingDefinition LoadHighlighting(string xshd)
    {
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    TunnelViewModel? _vm;
    bool _syncingEditor;

    public TunnelCard()
    {
        InitializeComponent();
        ConfEditor.SyntaxHighlighting = ConfHighlighting;
        ConfEditor.TextChanged += (_, _) =>
        {
            RunLint(ConfEditor.Text);
            if (_syncingEditor || _vm is null) return;
            _vm.ConfigText = ConfEditor.Text;
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.PropertyChanged -= OnVmPropertyChanged; HookEditCollections(_vm, false); _vm.RemovalAnimator = null; }
            _vm = DataContext as TunnelViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                HookEditCollections(_vm, true);
                _vm.RemovalAnimator = PlayRemove;
            }
            BuildDetail();
            ApplyCardAccent();
            // Initial state (no animation): show the right content at its natural height.
            var editing = _vm?.IsEditing ?? false;
            ExpandContent.IsVisible = editing;
            DetailPanel.IsVisible = !editing;
            ExpandContent.Opacity = 1; // never leave a pane sub-opacity if a reveal was superseded
            DetailPanel.Opacity = 1;
            Body.Height = double.NaN; // auto — sizes to content
        };
        // Re-resolve a per-card "mono" accent when the app theme flips.
        ActualThemeVariantChanged += (_, _) => ApplyCardAccent();
        // PointerPressed instead of Tapped: Tapped suppresses the second of two fast
        // clicks (double-tap detection), which made rapid expand/collapse feel dead.
        AddHandler(PointerPressedEvent, OnCardPressed, handledEventsToo: false);

        // Wrapping content (chips, wrap rows) changes height without any collection
        // event — e.g. a long value wraps to a second line, or the window narrows.
        // Animate the card to follow instead of snapping (or clipping).
        DetailPanel.SizeChanged += OnBodyContentSizeChanged;
        ExpandContent.SizeChanged += OnBodyContentSizeChanged;

        // Interface addresses sit next to the keys while they fit; when they'd wrap, the
        // whole group drops to its own "Addresses" line below.
        IfaceGrid.SizeChanged += (_, _) => UpdateAddressLayout();
        AddrList.SizeChanged += (_, _) => UpdateAddressLayout();

        // Fade a card in the first time it appears; fade + collapse it on removal.
        ClipToBounds = true;
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(AnimMs), Easing = Motion.Standard },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(AnimMs), Easing = Motion.Standard },
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (!_appeared) { _appeared = true; Opacity = 1; }
            Dispatcher.UIThread.Post(UpdateAddressLayout, DispatcherPriority.Loaded);
            // First-render reconcile: a freshly attached card's auto-sized body can come
            // up collapsed to its header (the detail measured 0 before the first layout
            // completed) until something re-drives its height. Snap it to the measured
            // content height once everything is loaded.
            Dispatcher.UIThread.Post(() =>
            {
                if (_vm is null || !double.IsNaN(Body.Height)) return;
                var content = _vm.IsEditing ? (Control)ExpandContent : DetailPanel;
                var to = NaturalHeight(content, Body.Bounds.Width < 1 ? 400 : Body.Bounds.Width);
                if (to > 0 && Math.Abs(Body.Bounds.Height - to) > 0.5)
                    TweenBodyToContent();
            }, DispatcherPriority.Loaded);
        };
    }

    bool _appeared;

    // Live lint for the raw editor: parser warnings plus the sanity checks the form
    // surfaces as route warnings. Purely advisory — Save stays available (the config is
    // validated for real when the tunnel is turned on).
    void RunLint(string text)
    {
        // "line N — " prefix where the offending key/value pair can be located.
        var lines = text.Split('\n');
        string At(string key, string? value)
        {
            for (int n = 0; n < lines.Length; n++)
            {
                if (!lines[n].TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (value is null || lines[n].Contains(value, StringComparison.OrdinalIgnoreCase))
                    return $"line {n + 1} — ";
            }
            return "";
        }

        string result;
        try
        {
            var parsed = Models.WireGuardConf.Parse(text);
            var problems = new List<string>(parsed.Warnings);
            if (!PeerViewModel.IsValidKey(parsed.PrivateKey))
                problems.Add($"{At("PrivateKey", null)}PrivateKey is not a valid 32-byte base64 key");
            int i = 0;
            foreach (var p in parsed.Peers)
            {
                i++;
                var label = p.Name ?? $"peer {i}";
                if (!PeerViewModel.IsValidKey(p.PublicKey))
                    problems.Add($"{At("PublicKey", p.PublicKey.Length > 0 ? p.PublicKey : null)}{label}: PublicKey is not a valid 32-byte base64 key");
                if (p.AllowedIps.Count == 0)
                    problems.Add($"{label}: no AllowedIPs");
                if (p.PingHost is not null && System.Net.IPAddress.TryParse(p.PingHost, out var ping)
                    && !p.AllowedIps.Any(c => Models.WireGuardConf.CidrContains(c, ping)))
                    problems.Add($"{At("PingHost", p.PingHost)}{label}: PingHost {p.PingHost} is outside AllowedIPs — probes wouldn't test the tunnel");
                if (p.Dns is not null && System.Net.IPAddress.TryParse(p.Dns, out var dns)
                    && !p.AllowedIps.Any(c => Models.WireGuardConf.CidrContains(c, dns)))
                    problems.Add($"{At("DNS", p.Dns)}{label}: DNS {p.Dns} is outside AllowedIPs — queries would leak outside the tunnel");
            }
            result = problems.Count == 0 ? "" : "● " + string.Join("\n● ", problems);
        }
        catch (Exception ex)
        {
            result = $"● parse error: {ex.Message}";
        }
        LintText.Text = result;
        LintBar.IsVisible = result.Length > 0;
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Push VM text into the editor when entering text mode (or on external updates).
        if (e.PropertyName is nameof(TunnelViewModel.ConfigText) or nameof(TunnelViewModel.IsTextMode))
        {
            if (_vm is null) return;
            if (e.PropertyName == nameof(TunnelViewModel.ConfigText) && ConfEditor.Text == _vm.ConfigText) return;
            _syncingEditor = true;
            ConfEditor.Text = _vm.ConfigText;
            _syncingEditor = false;
        }
        // Switching raw editor <-> fields resizes the body and slides the new pane in.
        if (e.PropertyName == nameof(TunnelViewModel.IsTextMode) && _vm is { IsEditing: true })
        {
            TweenBodyToContent();
            // Fields -> raw slides in from the right; raw -> fields from the left.
            SlideIn(_vm.IsTextMode ? EditorHost : FieldsGrid, _vm.IsTextMode ? 36 : -36);
        }
        if (e.PropertyName == nameof(TunnelViewModel.CollapsedSummary))
            BuildDetail();
        // Keep the collapsed detail's per-peer handshake/RTT live; skip while editing
        // (the detail panel is hidden then).
        if (e.PropertyName == nameof(TunnelViewModel.StatsTick) && _vm is { IsEditing: false })
            BuildDetail();
        if (e.PropertyName == nameof(TunnelViewModel.Accent))
            ApplyCardAccent();
        if (e.PropertyName == nameof(TunnelViewModel.IsEditing) && _vm is not null)
        {
            SwapBody(_vm.IsEditing);
            if (_vm.IsEditing) ScrollSelfIntoView(); // starts now, in sync with the expand
        }
    }

    // Swap the visible body pane and animate the card height to the new content. Only the
    // target pane is ever visible, so the growing/shrinking clip reveals it cleanly — no
    // opacity crossfade (which glitched when the target's measured height changed mid-run).
    void SwapBody(bool editing)
    {
        var show = editing ? (Control)ExpandContent : DetailPanel;
        var hide = editing ? (Control)DetailPanel : ExpandContent;
        var gen = ++_animGen;
        var from = Body.Bounds.Height;
        hide.IsVisible = false;
        show.IsVisible = true;
        var w = Body.Bounds.Width;
        if (w < 1) w = 400;
        var to = NaturalHeight(show, w);
        if (to < 1 || from < 1 || Math.Abs(to - from) < 0.5)
        {
            // No height change to ride — just show the pane, no reveal.
            show.Opacity = 1;
            if (show.RenderTransform is TranslateTransform t0) t0.Y = 0;
            Body.Height = double.NaN;
            return;
        }
        // Reveal the incoming pane in step with the height tween: fade + a small slide up.
        // Only one pane is ever visible (fade-in, not the crossfade that glitched); the
        // reveal is a RenderTransform + Opacity so it never perturbs the measured height.
        var shift = new TranslateTransform(0, RevealShift);
        show.RenderTransform = shift;
        show.Opacity = 0;
        Tween(0, 1, AnimMs,
            v => { if (_animGen == gen) { show.Opacity = v; shift.Y = RevealShift * (1 - v); } },
            () =>
            {
                if (_animGen != gen) return;
                show.Opacity = 1;
                if (ReferenceEquals(show.RenderTransform, shift)) show.RenderTransform = null;
            });
        Body.Height = from;
        Tween(from, to, AnimMs,
            v => { if (_animGen == gen) Body.Height = v; },
            () =>
            {
                if (_animGen != gen) return;
                Body.Height = double.NaN;
                _postTween.Restart();
            });
    }

    // Thin forwarder to the shared Motion.Tween, so the whole app has one tween loop and one
    // easing curve. Kept because callers here and in MainWindow/PeerBlock reference TunnelCard.Tween.
    internal static void Tween(double from, double to, int ms, Action<double> apply, Action? done = null)
        => Motion.Tween(from, to, ms, apply, done);

    // Slide a pane in horizontally — used for the raw <-> fields swap.
    void SlideIn(Control pane, double fromX)
    {
        var tt = new TranslateTransform(fromX, 0);
        pane.RenderTransform = tt;
        Tween(fromX, 0, AnimMs, v => tt.X = v, () => { if (ReferenceEquals(pane.RenderTransform, tt)) pane.RenderTransform = null; });
    }

    // Scroll the card near the top of the list, animated in step with the expand.
    void ScrollSelfIntoView()
    {
        var sv = this.FindAncestorOfType<ScrollViewer>();
        if (sv?.Content is not Visual content) return;
        var p = this.TranslatePoint(new Point(0, 0), content);
        if (p is not { } pt) return;
        Tween(sv.Offset.Y, Math.Max(0, pt.Y - 12), AnimMs, v => sv.Offset = new Vector(sv.Offset.X, Math.Max(0, v)));
    }

    // ---- per-card accent -------------------------------------------------------

    void ApplyCardAccent()
    {
        var name = _vm?.Accent;
        if (string.IsNullOrEmpty(name))
        {
            // Follow the global accent: drop any local override.
            Resources.Remove("AccentBrush");
            Resources.Remove("AccentDimBrush");
            Resources.Remove("OnAccentBrush");
            return;
        }
        var color = Accents.Resolve(name, ActualThemeVariant == ThemeVariant.Dark);
        Resources["AccentBrush"] = new SolidColorBrush(color);
        Resources["AccentDimBrush"] = new SolidColorBrush(color, 0.4);
        Resources["OnAccentBrush"] = new SolidColorBrush(Accents.On(color));
    }

    // ---- expand/collapse: explicit height animation of a single region ----------

    const int AnimMs = Motion.SlowMs;              // structural motion (expand/collapse/reveal)
    const double RevealShift = Motion.RevealShift; // px the incoming pane slides up from as it fades in
    int _animGen; // cancels a stale animation finalize on rapid re-toggle

    static double NaturalHeight(Control content, double width)
    {
        content.Measure(new Size(width, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    // Subscribe to the editable collections so adding/removing peers, domains, allowed IPs
    // or addresses animates the card's resize (same tween as expand/collapse).
    void HookEditCollections(TunnelViewModel vm, bool add)
    {
        if (add)
        {
            vm.Peers.CollectionChanged += OnPeersChanged;
            vm.Addresses.CollectionChanged += OnContentChanged;
            foreach (var p in vm.Peers) HookPeer(p, true);
        }
        else
        {
            vm.Peers.CollectionChanged -= OnPeersChanged;
            vm.Addresses.CollectionChanged -= OnContentChanged;
            foreach (var p in vm.Peers) HookPeer(p, false);
        }
    }

    void HookPeer(PeerViewModel p, bool add)
    {
        if (add) { p.Domains.CollectionChanged += OnContentChanged; p.AllowedIps.CollectionChanged += OnContentChanged; }
        else { p.Domains.CollectionChanged -= OnContentChanged; p.AllowedIps.CollectionChanged -= OnContentChanged; }
    }

    void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (PeerViewModel p in e.OldItems) HookPeer(p, false);
        if (e.NewItems is not null) foreach (PeerViewModel p in e.NewItems) HookPeer(p, true);
        OnContentChanged(sender, e);
    }

    void OnContentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.IsEditing == true) TweenBodyToContent();
    }

    // A driven tween measures its target before release; the post-release layout can
    // differ by a hair, and re-animating that correction reads as an overshoot bounce.
    readonly Stopwatch _postTween = new();

    // Only reacts while the body is in auto mode: a non-NaN Height means an animation is
    // already driving it (and re-triggering off its own frames would loop). The first
    // layout (0 → natural) is skipped — cards appear via the opacity fade, not a grow.
    void OnBodyContentSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control { IsVisible: false }) return;
        if (!double.IsNaN(Body.Height)) return;
        if (_postTween.IsRunning && _postTween.ElapsedMilliseconds < 150) return;
        if (e.PreviousSize.Height < 0.5) return;
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;
        TweenBodyToContent(e.PreviousSize.Height);
    }

    // Fade + collapse the card, then run the real removal.
    bool PlayRemove(Action complete)
    {
        _appeared = true; // don't re-trigger the appear fade
        Height = Bounds.Height;      // fix height so we can shrink it
        Opacity = 0;
        Dispatcher.UIThread.Post(() => Height = 0, DispatcherPriority.Render);
        DispatcherTimer.RunOnce(complete, TimeSpan.FromMilliseconds(AnimMs + Motion.CushionMs));
        return true;
    }

    // Animate the shared region's Height from where it is now to the visible content's
    // natural height, then release it back to Auto so later edits resize freely. The
    // target is measured synchronously (Measure works without waiting for a layout
    // pass) — the old deferred-post version could be pre-empted and skip the animation.
    // Plain property tween — an Animation with FillMode.Forward keeps clamping Height
    // even after setting it back to NaN, which froze the card and clipped content.
    void TweenBodyToContent(double? fromOverride = null)
    {
        if (_vm is null) return;
        var gen = ++_animGen;
        var from = fromOverride ?? Body.Bounds.Height;
        var w = Body.Bounds.Width;
        if (w < 1) w = 400;
        var content = _vm.IsEditing ? (Control)ExpandContent : DetailPanel;
        var to = NaturalHeight(content, w);
        if (to < 1 || from < 1 || Math.Abs(to - from) < 0.5)
        {
            Body.Height = double.NaN;
            return;
        }

        Body.Height = from;
        Tween(from, to, AnimMs,
            v => { if (_animGen == gen) Body.Height = v; },
            () =>
            {
                if (_animGen != gen) return;
                Body.Height = double.NaN; // back to auto
                _postTween.Restart();
            });
    }

    // Syntax-colored collapsed detail as atomic tokens in a WrapPanel: addresses in
    // IP color, domains in domain color. Each token is a whole TextBlock, so wrapping
    // moves a full address/domain to the next line and never splits one.
    bool _addrStacked;
    bool _measuringAddr;

    // Keep the interface addresses beside the keys until they'd wrap, then move the whole
    // group to a labelled "Addresses" line. Compares their single-line width against the
    // space left next to the key group.
    void UpdateAddressLayout()
    {
        if (_measuringAddr || _vm is null || !_vm.ShowInterfaceSection) return;
        var gridW = IfaceGrid.Bounds.Width;
        if (gridW < 1) return;
        _measuringAddr = true;
        try
        {
            AddrList.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var need = AddrList.DesiredSize.Width;
            var avail = gridW - KeyGroup.Bounds.Width - 8;
            // Hysteresis so a borderline width can't oscillate between the two layouts.
            var stacked = _addrStacked ? need > avail + 12 : need > avail;
            if (stacked == _addrStacked && AddrLabel.IsVisible == stacked) return;
            _addrStacked = stacked;
            Grid.SetRow(AddrList, stacked ? 1 : 0);
            Grid.SetColumn(AddrList, 1);
            AddrList.Margin = new Avalonia.Thickness(stacked ? 0 : 0, stacked ? 1 : 0, 0, 0);
            AddrLabel.IsVisible = stacked;
        }
        finally { _measuringAddr = false; }
    }

    void BuildDetail()
    {
        DetailPanel.Children.Clear();
        if (_vm is null) return;

        IBrush accent = this.TryFindResource("AccentBrush", out var ao) && ao is IBrush ab ? ab : Brushes.LimeGreen;

        TextBlock Mono(string text, IBrush brush)
        {
            // "mono" class → FontSize follows the zoom-scaled Fs12 resource.
            var tb = new TextBlock { Text = text, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };
            tb.Classes.Add("mono");
            return tb;
        }

        // A soft filled pill (no border), used for routes and domains — same look as the
        // edit-mode chips, tinted to its text color.
        Border Pill(string text, IBrush fg)
        {
            IBrush bg = fg is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.15) : Brushes.Transparent;
            var tb = Mono(text, fg);
            tb.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            return new Border
            {
                Background = bg,
                CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Avalonia.Thickness(7, 2),
                Margin = new Avalonia.Thickness(0, 0, 5, 4),
                Child = tb,
            };
        }

        // "label   value(s)" row: a muted label in a fixed column, content wrapping beside it.
        void LabeledRow(string label, IReadOnlyList<Control> content)
        {
            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(0, 0, 0, 3),
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            var lbl = new TextBlock { Text = label, Width = 50, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Avalonia.Thickness(0, 2, 8, 0) };
            lbl.Classes.Add("lbl");
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);
            var wrap = new WrapPanel();
            foreach (var c in content) wrap.Children.Add(c);
            Grid.SetColumn(wrap, 1);
            grid.Children.Add(wrap);
            DetailPanel.Children.Add(grid);
        }

        // The peer's name (bold accent) with its live status flushed right — uptime, transfer
        // totals and RTT while connected, or dots when it isn't.
        void NameLine(string name, string stats, IBrush accent)
        {
            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(0, 0, 0, 4),
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            var nm = Mono(name, accent);
            nm.FontWeight = Avalonia.Media.FontWeight.Bold;
            Grid.SetColumn(nm, 0);
            grid.Children.Add(nm);
            var st = new TextBlock
            {
                Text = stats, Opacity = 0.5,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                TextAlignment = Avalonia.Media.TextAlignment.Right,
            };
            st.Classes.Add("mono");
            Grid.SetColumn(st, 1);
            grid.Children.Add(st);
            DetailPanel.Children.Add(grid);
        }

        // The DNS server as a bubble like the domains. When it's pinned as the device-wide
        // (system) DNS, it's highlighted with the accent and a pin glyph instead of the plain
        // blue bubble.
        Control DnsValue(PeerViewModel peer)
        {
            var dns = peer.Dns.Trim();
            if (!peer.IsPinned) return Pill(dns, Syntax.IpBrush);

            var pin = new TextBlock { Text = "", FontSize = 11, Foreground = accent, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            pin.Classes.Add("glyph");
            var txt = Mono(dns, accent);
            txt.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            row.Children.Add(pin);
            row.Children.Add(txt);
            var bg = accent is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.18) : (IBrush)Brushes.Transparent;
            return new Border
            {
                Background = bg,
                CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Avalonia.Thickness(7, 2),
                Margin = new Avalonia.Thickness(0, 0, 5, 4),
                Child = row,
            };
        }

        // A soft, self-fading divider between peers.
        void AddSeparator()
        {
            DetailPanel.Children.Add(new Border
            {
                Height = 1,
                Background = (IBrush?)this.FindResource("SeparatorBrush"),
                Margin = new Avalonia.Thickness(0, 6, 0, 8),
            });
        }

        // One block per peer: a bold name + right-aligned live status line, then labelled
        // "routes" and "dns" rows whose values flow left as soft pills. The metric-preferred
        // (or live-active) failover route is tinted with the accent and marked "· active",
        // and pulled out of the plain route list so nothing is duplicated. Peers separate
        // with a hairline.
        bool first = true;
        for (int i = 0; i < _vm.Peers.Count; i++)
        {
            var p = _vm.Peers[i];
            var wg = !_vm.IsExternal && !_vm.IsCustom;
            if (!first) AddSeparator();
            first = false;

            if (wg)
            {
                var name = string.IsNullOrWhiteSpace(p.Name)
                    ? (_vm.Peers.Count > 1 ? $"peer {i + 1}" : "peer")
                    : p.Name.Trim();
                string stats;
                if (p.HasStats)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(p.UptimeText)) parts.Add(p.UptimeText);
                    parts.Add($"↑ {p.TxTotalText}  ↓ {p.RxTotalText}");
                    if (p.HasPingHost && !string.IsNullOrEmpty(p.PingText)) parts.Add(p.PingText);
                    stats = string.Join("   ·   ", parts);
                }
                else stats = "·····";
                NameLine(name, stats, accent);

                var isActiveMember = p.FailoverRole == "active"
                    || (string.IsNullOrEmpty(p.FailoverRole) && _vm.Host.RouteGroupInfo(p) is { Position: 1 });
                IReadOnlyList<string> activeRoutes = isActiveMember
                    ? _vm.Host.RouteGroupCidrs(p)
                    : System.Array.Empty<string>();
                var routePills = new List<Control>();
                foreach (var a in activeRoutes) routePills.Add(Pill($"{a} · active", accent));
                foreach (var ip in p.AllowedIpValues.Where(ip => !activeRoutes.Contains(Models.WireGuardConf.CanonicalCidr(ip))))
                    routePills.Add(Pill(ip, Syntax.IpBrush));
                if (routePills.Count > 0) LabeledRow("routes", routePills);
            }

            if (p.HasDns || p.DomainValues.Any())
            {
                var content = new List<Control>();
                if (p.HasDns) content.Add(DnsValue(p));
                foreach (var d in p.DomainValues) content.Add(Pill(d, Syntax.DomainBrush));
                LabeledRow("dns", content);
            }
        }

        if (DetailPanel.Children.Count == 0)
            LabeledRow("", new List<Control> { Mono("no peers configured", Syntax.IpBrush) });
    }

    // Collapsed: a click anywhere on the card expands it. Expanded: only a click on
    // the header bar collapses it (Save/Cancel/delete/other-card-open also collapse);
    // body clicks do nothing. The connect control and other controls never toggle.
    void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TunnelViewModel vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var inHeader = false;
        for (var el = e.Source as Avalonia.Visual; el is not null && el != this; el = el.GetVisualParent())
        {
            if (el is Button or ToggleButton or ToggleSwitch or TextBox or ComboBox) return;
            if (el == ConnectBox) return;
            if (el == HeaderRow) inHeader = true;
        }
        if (!vm.IsEditing) vm.BeginEditCommand.Execute(null);
        else if (inHeader) vm.CancelEditCommand.Execute(null);
    }

    // Clicking the state label toggles the connection (the switch's own area does too).
    void OnConnectLabelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is TunnelViewModel vm && !vm.IsEditing && !vm.IsExternal)
        {
            vm.IsConnected = !vm.IsConnected;
            e.Handled = true;
        }
    }
}
