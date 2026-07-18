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
            // Initial state (no animation): show the right content at its natural height.
            var editing = _vm?.IsEditing ?? false;
            ExpandContent.IsVisible = editing;
            DetailPanel.IsVisible = !editing;
            ExpandContent.Opacity = 1; // never leave a pane sub-opacity if a reveal was superseded
            DetailPanel.Opacity = 1;
            Body.Height = double.NaN; // auto — sizes to content
        };
        // Press arms a tap; the expand happens on release (see OnCard*), so a press that
        // collapsed an editing card can't re-expand it when released.
        AddHandler(PointerPressedEvent, OnCardPressed, handledEventsToo: false);
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

        // Fade a card in the first time it appears; fade + collapse it on removal. The fade lives
        // on CardShell (the shadow-casting Border, whose Opacity transition comes from the
        // Border.card style) and NEVER on this UserControl: an opacity layer on an ancestor clips
        // descendant BoxShadows to the ancestor's bounds, which cut every card shadow to a 1px rim.
        // Height-wise, a XAML Height DoubleTransition can't animate from the unset (NaN) auto
        // height, so PlayRemove drives the height with the shared code tween instead.
        // NB: ClipToBounds is left OFF here so the card's elevation shadow isn't swallowed; the
        // animated region is clipped by the inner Border.body, and PlayRemove turns clipping on
        // for the shrink (the shadow is fading out then anyway).
        CardShell.Opacity = 0;
        AttachedToVisualTree += (_, _) =>
        {
            // The elevation glow must survive every container between the card and the scroll
            // viewport (item ContentPresenter, items panel, ItemsPresenter, ItemsControl) — any
            // of them clipping flattens the card's side glow while the bottom bar's glows free
            // (its chain has none of these). The viewport itself keeps its clip: stop there.
            for (var v = this.GetVisualParent(); v is Control c && c is not Avalonia.Controls.Presenters.ScrollContentPresenter; v = v.GetVisualParent())
                c.ClipToBounds = false;
            if (!_appeared) { _appeared = true; CardShell.Opacity = 1; }
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
        }
        if (e.PropertyName == nameof(TunnelViewModel.IsEditing) && _vm is not null)
        {
            // Leaving edit: rebuild the detail first — theme/accent may have changed while the
            // detail was hidden (the StatsTick rebuild is skipped during edits), and the pills
            // snapshot brush instances, so an un-rebuilt detail would show the old palette.
            if (!_vm.IsEditing) BuildDetail();
            SwapBody(_vm.IsEditing);
            if (_vm.IsEditing) ScrollSelfIntoView(); // starts now, in sync with the expand
        }
    }

    // Swap the visible body pane and animate the card height to the new content.
    // EXPAND (detail -> edit): the edit pane becomes visible immediately and the growing clip
    // reveals it while it fades/slides in — a curtain opening.
    // COLLAPSE (edit -> detail): the edit pane STAYS visible while the card shrinks (the clip
    // eats it bottom-up — a curtain closing), and only at the settled height does the detail
    // swap in with the fade. Hiding the edit pane up front left the card animating a blank
    // void under the early-shown detail for the whole tween.
    void SwapBody(bool editing)
    {
        var show = editing ? (Control)ExpandContent : DetailPanel;
        var hide = editing ? (Control)DetailPanel : ExpandContent;
        var gen = ++_animGen;
        var from = Body.Bounds.Height;
        var w = Body.Bounds.Width;
        if (w < 1) w = 400;

        // Measure the target pane (it must be visible for Measure to see its content; for the
        // collapse path flip it back immediately — nothing renders between the two writes).
        var wasVisible = show.IsVisible;
        show.IsVisible = true;
        var to = NaturalHeight(show, w);
        if (!editing && !wasVisible) show.IsVisible = false;

        if (to < 1 || from < 1 || Math.Abs(to - from) < 0.5)
        {
            // No height change to ride — just swap the panes, no reveal.
            hide.IsVisible = false;
            show.IsVisible = true;
            show.Opacity = 1;
            if (show.RenderTransform is TranslateTransform t0) t0.Y = 0;
            Body.Height = double.NaN;
            return;
        }

        void RevealShow()
        {
            // Fade + small slide up for the incoming pane (RenderTransform + Opacity only, so
            // it never perturbs the measured height).
            var shift = new TranslateTransform(0, RevealShift);
            show.RenderTransform = shift;
            show.Opacity = 0;
            show.IsVisible = true;
            Tween(0, 1, AnimMs,
                v => { if (_animGen == gen) { show.Opacity = v; shift.Y = RevealShift * (1 - v); } },
                () =>
                {
                    if (_animGen != gen) return;
                    show.Opacity = 1;
                    if (ReferenceEquals(show.RenderTransform, shift)) show.RenderTransform = null;
                });
        }

        if (editing)
        {
            hide.IsVisible = false;
            RevealShow();               // reveal rides the growing clip
        }
        Body.Height = from;
        Tween(from, to, AnimMs,
            v => { if (_animGen == gen) Body.Height = v; },
            () =>
            {
                if (_animGen != gen) return;
                if (!editing)
                {
                    hide.IsVisible = false; // the curtain has fully closed over the edit pane
                    RevealShow();           // detail fades in at the settled height
                }
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
        CardShell.Opacity = 0; // fades via the Border.card style's Opacity transition
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

    // Set by the Android head: reflow the collapsed detail for a narrow, thumb-driven
    // screen — the cramped right-aligned peer-stats line becomes a 2-column label/value
    // grid. Desktop leaves this false and keeps the single-line strip.
    public static bool Compact;

    void BuildDetail()
    {
        DetailPanel.Children.Clear();
        if (_vm is null) return;

        // Detail text is accent-as-TEXT — use the contrast-safe variant (theme-keyed).
        IBrush accent = this.TryFindResource("AccentTextBrush", out var ao) && ao is IBrush ab ? ab : Brushes.LimeGreen;
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
                Text = stats, Opacity = 0.65,
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

        // Compact (mobile): the peer's live stats as a 2-column label/value grid instead of
        // a single crowded right-aligned line. Each cell is a muted label over a mono value.
        void StatGrid(IReadOnlyList<(string K, string V)> cells)
        {
            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(0, 2, 0, 6),
                ColumnDefinitions = new ColumnDefinitions("*,*"),
            };
            for (int r = 0; r <= (cells.Count - 1) / 2; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int idx = 0; idx < cells.Count; idx++)
            {
                var cell = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 0, 10, 6) };
                var kl = new TextBlock { Text = cells[idx].K };
                kl.Classes.Add("lbl");
                // Inherit the theme foreground (don't force a color) so the value reads as ink.
                var vl = new TextBlock { Text = cells[idx].V, Opacity = 0.92 };
                vl.Classes.Add("mono");
                cell.Children.Add(kl);
                cell.Children.Add(vl);
                Grid.SetColumn(cell, idx % 2);
                Grid.SetRow(cell, idx / 2);
                grid.Children.Add(cell);
            }
            DetailPanel.Children.Add(grid);
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
                if (Compact)
                {
                    // Name on its own line; stats drop into a 2-column grid below (no crowding).
                    NameLine(name, p.HasStats ? "" : "·····", accent);
                    if (p.HasStats)
                    {
                        var cells = new List<(string, string)>();
                        if (!string.IsNullOrEmpty(p.UptimeText)) cells.Add(("uptime", p.UptimeText));
                        if (p.HasPingHost && !string.IsNullOrEmpty(p.PingText)) cells.Add(("rtt", p.PingText));
                        cells.Add(("↑ sent", p.TxTotalText));
                        cells.Add(("↓ recv", p.RxTotalText));
                        StatGrid(cells);
                    }
                }
                else
                {
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
                }

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

    // ---- press / tap-to-expand ---------------------------------------------------

    bool _tapArmed;

    // Collapsed: a press arms a tap and the release expands. Expanded: a header press collapses
    // (cancel). Controls never arm anything. Arming on press + acting on release keeps a press
    // that collapsed an editing card from re-expanding it when released.
    void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TunnelViewModel vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var inHeader = false;
        for (var el = e.Source as Avalonia.Visual; el is not null && el != this; el = el.GetVisualParent())
        {
            if (el is Button or ToggleButton or TextBox or ComboBox) return;
            if (el == ConnectBox) return;
            if (el == HeaderRow) inHeader = true;
        }
        if (vm.IsEditing) { if (inHeader) vm.CancelEditCommand.Execute(null); return; }
        _tapArmed = true;
    }

    void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasArmed = _tapArmed; // false for editing-card presses (handled on press) and controls
        _tapArmed = false;
        if (wasArmed && DataContext is TunnelViewModel { IsEditing: false } vm)
            vm.BeginEditCommand.Execute(null); // a plain tap expands
    }

    void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _tapArmed = false;

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
