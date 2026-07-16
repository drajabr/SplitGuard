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
            UpdateSpark();
            // Initial state (no animation): show the right content at its natural height.
            var editing = _vm?.IsEditing ?? false;
            ExpandContent.IsVisible = editing;
            DetailPanel.IsVisible = !editing;
            ExpandContent.Opacity = 1; // never leave a pane sub-opacity if a reveal was superseded
            DetailPanel.Opacity = 1;
            Body.Height = double.NaN; // auto — sizes to content
        };
        // Press arms a tap-or-drag; the actual expand happens on release when no drag occurred,
        // so a mouse-drag on a collapsed user card can reorder the list instead (see OnCard*).
        AddHandler(PointerPressedEvent, OnCardPressed, handledEventsToo: false);
        AddHandler(PointerMovedEvent, OnCardPointerMoved, handledEventsToo: false);
        AddHandler(PointerReleasedEvent, OnCardPointerReleased, handledEventsToo: false);
        AddHandler(PointerCaptureLostEvent, OnCardCaptureLost, handledEventsToo: false);

        // Wrapping content (chips, wrap rows) changes height without any collection
        // event — e.g. a long value wraps to a second line, or the window narrows.
        // Animate the card to follow instead of snapping (or clipping).
        DetailPanel.SizeChanged += OnBodyContentSizeChanged;
        ExpandContent.SizeChanged += OnBodyContentSizeChanged;

        // Interface addresses sit next to the keys while they fit; when they'd wrap, the
        // whole group drops to its own "Addresses" line below.
        IfaceGrid.SizeChanged += (_, _) => UpdateAddressLayout();
        AddrList.SizeChanged += (_, _) => UpdateAddressLayout();

        // Fade a card in the first time it appears; fade + collapse it on removal. Only Opacity
        // gets a XAML transition — a Height DoubleTransition can't animate from the unset (NaN)
        // auto height (every lerp frame is NaN, so the collapse held for the full duration and
        // snapped); PlayRemove drives the height with the shared code tween instead.
        // NB: ClipToBounds is left OFF here so the card's elevation shadow isn't swallowed; the
        // animated region is clipped by the inner Border.body, and PlayRemove turns clipping on
        // for the shrink (the shadow is fading out then anyway).
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(AnimMs), Easing = Motion.Standard },
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
        if (e.PropertyName == nameof(TunnelViewModel.StatsTick))
        {
            if (_vm is { IsEditing: false }) BuildDetail();
            UpdateSpark();
        }
        if (e.PropertyName == nameof(TunnelViewModel.StatsVisible))
            UpdateSpark();
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

    // ---- header sparkline --------------------------------------------------------

    // Redraw the rolling-throughput polyline: samples map left-to-right, y scaled to the
    // window's own max so the shape always uses the full height (a flat idle line sits at
    // the bottom). Hidden until there are at least two samples to connect.
    void UpdateSpark()
    {
        var h = _vm?.RateHistory;
        if (_vm is null || !_vm.StatsVisible || h is null || h.Count < 2)
        {
            Spark.IsVisible = false;
            return;
        }
        var max = Math.Max(h.Max(), 1.0);
        var pts = new global::Avalonia.Points();
        for (int i = 0; i < h.Count; i++)
            pts.Add(new Point(i * 56.0 / (TunnelViewModel.SparkCapacity - 1), 13 - h[i] / max * 12));
        Spark.Points = pts;
        Spark.IsVisible = true;
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
    // A peer block drives its own clipped height curtain on expand/collapse; while it does, the
    // outer body must NOT also start a follow tween (two tweens on the same height fight and the
    // collapse snaps). The peer brackets its animation with these, and the auto-height body just
    // tracks the peer's size frame by frame.
    int _suppressBodySize;
    internal void BeginBodySuppression() => _suppressBodySize++;
    internal void EndBodySuppression() { if (_suppressBodySize > 0) _suppressBodySize--; }

    void OnBodyContentSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_suppressBodySize > 0) return;
        if (sender is Control { IsVisible: false }) return;
        if (!double.IsNaN(Body.Height)) return;
        if (_postTween.IsRunning && _postTween.ElapsedMilliseconds < 150) return;
        if (e.PreviousSize.Height < 0.5) return;
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;
        TweenBodyToContent(e.PreviousSize.Height);
    }

    // Fade + collapse the card, then run the real removal. The height is driven by the shared
    // code tween (a XAML Height transition would lerp from the unset NaN auto height and snap).
    bool PlayRemove(Action complete)
    {
        _appeared = true; // don't re-trigger the appear fade
        ClipToBounds = true; // clip content to the shrinking height (shadow is fading out anyway)
        Opacity = 0;      // fades via the Opacity transition
        Motion.Tween(Bounds.Height, 0, AnimMs, v => Height = v);
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
        // Theme-aware syntax brushes (fall back to the fixed palette if unset) so IPs/domains keep
        // contrast on light themes instead of washing out.
        IBrush ipBrush = this.TryFindResource("SynIpBrush", out var ipo) && ipo is IBrush ipb ? ipb : Syntax.IpBrush;
        IBrush domainBrush = this.TryFindResource("SynDomainBrush", out var dmo) && dmo is IBrush dmb ? dmb : Syntax.DomainBrush;

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
                CornerRadius = new Avalonia.CornerRadius(6),
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
            if (!peer.IsPinned) return Pill(dns, ipBrush);

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
                CornerRadius = new Avalonia.CornerRadius(6),
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
                    routePills.Add(Pill(ip, ipBrush));
                if (routePills.Count > 0) LabeledRow("routes", routePills);
            }

            if (p.HasDns || p.DomainValues.Any())
            {
                var content = new List<Control>();
                if (p.HasDns) content.Add(DnsValue(p));
                foreach (var d in p.DomainValues) content.Add(Pill(d, domainBrush));
                LabeledRow("dns", content);
            }
        }

        if (DetailPanel.Children.Count == 0)
            LabeledRow("", new List<Control> { Mono("no peers configured", ipBrush) });
    }

    // ---- press / tap-to-expand / drag-to-reorder --------------------------------

    ItemsControl? _list;
    bool _dragArmed, _dragging;
    Point _pressPt;               // grab point in card space (preserves grab offset while dragging)
    TranslateTransform? _dragXf;

    // Collapsed: press arms a tap-or-drag; a real drag (moved past threshold) reorders, otherwise
    // release expands. Expanded: a header press collapses (cancel). Controls never arm anything.
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
        if (vm.IsEditing) { if (inHeader) vm.CancelEditCommand.Execute(null); return; }

        _pressPt = e.GetPosition(this);
        _dragArmed = true;
        _dragging = false;
        _list = this.FindAncestorOfType<ItemsControl>();
        e.Pointer.Capture(this);
    }

    void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _dragging) { if (_dragging) DragTo(e); return; }
        if (DataContext is not TunnelViewModel vm) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.Y - _pressPt.Y) < 6 && Math.Abs(p.X - _pressPt.X) < 6) return; // movement threshold
        // Only user tunnels reorder, and only when there's more than one to shuffle.
        if (vm.IsCustom || vm.IsExternal || _list is null || vm.Host.ReorderableCount < 2)
        {
            _dragArmed = false;
            return;
        }
        _dragging = true;
        _dragXf = new TranslateTransform();
        RenderTransform = _dragXf;
        Opacity = 0.9;
        if (this.GetVisualParent() is Control container) container.ZIndex = 1; // float above siblings
        DragTo(e);
    }

    // Follow the pointer (re-based off the container's live layout position so a reorder mid-drag
    // doesn't make the card jump) and move to whichever user slot the pointer is over.
    void DragTo(PointerEventArgs e)
    {
        if (_list is null || _dragXf is null || DataContext is not TunnelViewModel vm) return;
        var pY = e.GetPosition(_list).Y;
        if (this.GetVisualParent() is Control container)
        {
            var top = container.TranslatePoint(new Point(0, 0), _list)?.Y ?? 0;
            _dragXf.Y = pY - _pressPt.Y - top; // keep the grabbed point under the pointer
        }
        foreach (var c in _list.GetRealizedContainers())
        {
            if (c.DataContext is not TunnelViewModel tvm || tvm.IsCustom || tvm.IsExternal) continue;
            var t = c.TranslatePoint(new Point(0, 0), _list)?.Y ?? 0;
            if (pY >= t && pY < t + c.Bounds.Height)
            {
                int idx = _list.IndexFromContainer(c);
                if (idx >= 0) vm.Host.MoveTunnel(vm, idx);
                break;
            }
        }
    }

    void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(tapped: true);
    void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(tapped: false);

    void EndDrag(bool tapped)
    {
        var wasDragging = _dragging;
        var wasArmed = _dragArmed; // false for editing-card presses (handled on press) and controls
        _dragArmed = false;
        _dragging = false;
        if (wasDragging)
        {
            var xf = _dragXf; _dragXf = null;
            Opacity = 1;
            if (this.GetVisualParent() is Control container) container.ZIndex = 0;
            if (xf is not null) // settle into the final slot
                Tween(xf.Y, 0, AnimMs, v => xf.Y = v,
                      () => { if (ReferenceEquals(RenderTransform, xf)) RenderTransform = null; });
            (DataContext as TunnelViewModel)?.Host.SaveTunnelOrder();
        }
        // Expand only if THIS press armed a tap on a collapsed card. Guarding on wasArmed stops a
        // press that collapsed an editing card (CancelEdit on press) from re-expanding it on release.
        else if (wasArmed && tapped && DataContext is TunnelViewModel { IsEditing: false } vm)
        {
            vm.BeginEditCommand.Execute(null); // a plain tap expands
        }
        _list = null;
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
