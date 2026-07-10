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

        // Fade a card in the first time it appears; fade + collapse it on removal.
        ClipToBounds = true;
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(AnimMs), Easing = new CubicEaseOut() },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(AnimMs), Easing = new CubicEaseOut() },
        };
        AttachedToVisualTree += (_, _) => { if (!_appeared) { _appeared = true; Opacity = 1; } };
    }

    bool _appeared;

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
        if (e.PropertyName == nameof(TunnelViewModel.Accent))
            ApplyCardAccent();
        if (e.PropertyName == nameof(TunnelViewModel.IsEditing) && _vm is not null)
        {
            ExpandContent.IsVisible = _vm.IsEditing;
            DetailPanel.IsVisible = !_vm.IsEditing;
            TweenBodyToContent();
            if (_vm.IsEditing) ScrollSelfIntoView(); // starts now, in sync with the expand
        }
    }

    // Simple CubicEaseOut tween driven by a timer — reliable for any target (transforms,
    // scroll offset) without the Animation/RunAsync quirks that crashed on transforms.
    // Render priority: default (background) ticks starve behind the heavy relayout a
    // collapse triggers (the VM rebuilds every peer), so the whole duration elapsed
    // before the first frame — the collapse snapped instead of animating.
    static void Tween(double from, double to, int ms, Action<double> apply, Action? done = null)
    {
        if (Math.Abs(to - from) < 0.5) { apply(to); done?.Invoke(); return; }
        var sw = Stopwatch.StartNew();
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            var p = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            var e = 1 - Math.Pow(1 - p, 3);
            apply(from + (to - from) * e);
            if (p >= 1) { timer!.Stop(); done?.Invoke(); }
        });
        timer.Start();
    }

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

    void OnDotPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is { IsEditing: true })
        {
            _vm.CycleAccent();
            e.Handled = true;
        }
    }

    // ---- expand/collapse: explicit height animation of a single region ----------

    const int AnimMs = 200;
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

    // Only reacts while the body is in auto mode: a non-NaN Height means an animation is
    // already driving it (and re-triggering off its own frames would loop). The first
    // layout (0 → natural) is skipped — cards appear via the opacity fade, not a grow.
    void OnBodyContentSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control { IsVisible: false }) return;
        if (!double.IsNaN(Body.Height)) return;
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
        DispatcherTimer.RunOnce(complete, TimeSpan.FromMilliseconds(AnimMs + 40));
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
            () => { if (_animGen == gen) Body.Height = double.NaN; }); // back to auto
    }

    // Syntax-colored collapsed detail as atomic tokens in a WrapPanel: addresses in
    // IP color, domains in domain color. Each token is a whole TextBlock, so wrapping
    // moves a full address/domain to the next line and never splits one.
    void BuildDetail()
    {
        DetailPanel.Children.Clear();
        if (_vm is null) return;

        TextBlock Mono(string text, IBrush brush)
        {
            // "mono" class → FontSize follows the zoom-scaled Fs12 resource.
            var tb = new TextBlock { Text = text, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };
            tb.Classes.Add("mono");
            return tb;
        }

        TextBlock Label(string text)
        {
            var tb = new TextBlock { Text = text };
            tb.Classes.Add("lbl");
            tb.Margin = new Avalonia.Thickness(0, 0, 6, 0);
            return tb;
        }

        // One row: an optional left label + left value, and a right-aligned value.
        void AddRow(string leftLabel, string left, IBrush leftBrush, string right, IBrush rightBrush)
        {
            var row = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 2) };
            if (right.Length > 0)
            {
                var r = Mono(right, rightBrush);
                r.Margin = new Avalonia.Thickness(12, 0, 0, 0);
                r.TextAlignment = Avalonia.Media.TextAlignment.Right;
                DockPanel.SetDock(r, Dock.Right);
                row.Children.Add(r);
            }
            if (leftLabel.Length > 0) row.Children.Add(Label(leftLabel));
            if (left.Length > 0) row.Children.Add(Mono(left, leftBrush));
            DetailPanel.Children.Add(row);
        }

        // Line 1: our address(es) on the left, all peers' allowed IPs on the right.
        var ourIps = string.Join(", ", _vm.AddressValues);
        var allowed = string.Join(", ", _vm.Peers.SelectMany(p => p.AllowedIpValues).Distinct());
        if (ourIps.Length > 0 || allowed.Length > 0) AddRow("Address", ourIps, Syntax.IpBrush, allowed, Syntax.IpBrush);

        // Then one line per peer that defines a DNS server and/or domains:
        //   "DNS" + server on the left, the domains it resolves right-aligned.
        foreach (var p in _vm.Peers)
        {
            var domains = string.Join(", ", p.DomainValues);
            if (!p.HasDns && domains.Length == 0) continue;
            AddRow(p.HasDns ? "DNS" : "", p.HasDns ? p.Dns.Trim() : "", Syntax.IpBrush, domains, Syntax.DomainBrush);
        }

        if (DetailPanel.Children.Count == 0)
            AddRow("", "no split DNS configured", Syntax.IpBrush, "", Syntax.IpBrush);
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
