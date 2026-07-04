using System.ComponentModel;
using System.Xml;
using Avalonia;
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
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as TunnelViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
            BuildDetail();

            // External and custom cards have no interface column: peers span full width.
            if (_vm is { ShowInterfaceSection: false })
            {
                Grid.SetColumn(PeersHost, 0);
                Grid.SetColumnSpan(PeersHost, 2);
                PeersHost.BorderThickness = new Avalonia.Thickness(0);
            }
            else
            {
                Grid.SetColumn(PeersHost, 1);
                Grid.SetColumnSpan(PeersHost, 1);
                PeersHost.BorderThickness = new Avalonia.Thickness(1, 0, 0, 0);
            }

            ApplyCardAccent();
            // Set the initial reveal state without animating (collapsed detail shown).
            var editing = _vm?.IsEditing ?? false;
            WithoutTransitions(ExpandHost, () => ExpandHost.MaxHeight = editing ? double.PositiveInfinity : 0);
            WithoutTransitions(CollapseHost, () => CollapseHost.MaxHeight = editing ? 0 : double.PositiveInfinity);
        };
        // Re-resolve a per-card "mono" accent when the app theme flips.
        ActualThemeVariantChanged += (_, _) => ApplyCardAccent();
        // PointerPressed instead of Tapped: Tapped suppresses the second of two fast
        // clicks (double-tap detection), which made rapid expand/collapse feel dead.
        AddHandler(PointerPressedEvent, OnCardPressed, handledEventsToo: false);
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Push VM text into the editor when entering text mode (or on external updates).
        if (e.PropertyName is nameof(TunnelViewModel.ConfigText) or nameof(TunnelViewModel.IsTextMode))
        {
            if (_vm is null) return;
            // Entering text mode: lock the editor to the exact height the fields area
            // currently occupies, so toggling modes causes no vertical jump. Read the
            // bounds synchronously (before the visibility relayout) then apply.
            if (e.PropertyName == nameof(TunnelViewModel.IsTextMode))
            {
                if (_vm.IsTextMode)
                {
                    var h = FieldsGrid.Bounds.Height;
                    if (h > 40) EditorHost.Height = h;
                }
                else
                {
                    EditorHost.ClearValue(HeightProperty);
                }
            }
            // Always push on mode entry; otherwise only when the texts diverge.
            if (e.PropertyName == nameof(TunnelViewModel.ConfigText) && ConfEditor.Text == _vm.ConfigText) return;
            _syncingEditor = true;
            ConfEditor.Text = _vm.ConfigText;
            _syncingEditor = false;
        }
        if (e.PropertyName == nameof(TunnelViewModel.CollapsedSummary))
            BuildDetail();
        if (e.PropertyName == nameof(TunnelViewModel.Accent))
            ApplyCardAccent();
        if (e.PropertyName == nameof(TunnelViewModel.IsEditing) && _vm is not null)
        {
            var editing = _vm.IsEditing;
            Animate(ExpandHost, ExpandContent, editing, _expandTok);
            Animate(CollapseHost, DetailPanel, !editing, _collapseTok);
        }
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
            return;
        }
        var color = Accents.Resolve(name, ActualThemeVariant == ThemeVariant.Dark);
        Resources["AccentBrush"] = new SolidColorBrush(color);
        Resources["AccentDimBrush"] = new SolidColorBrush(color, 0.4);
    }

    void OnDotPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is { IsEditing: true })
        {
            _vm.CycleAccent();
            e.Handled = true;
        }
    }

    // ---- expand/collapse: animate MaxHeight to the exact content height ---------

    // Independent generation counters so a rapid re-toggle cancels a pending finalize.
    sealed class Tok { public int V; }
    readonly Tok _expandTok = new();
    readonly Tok _collapseTok = new();

    static void WithoutTransitions(Border b, Action apply)
    {
        var t = b.Transitions;
        b.Transitions = null;
        apply();
        b.Transitions = t;
    }

    // Open: animate 0 → measured content height, then lift the cap so the content can
    // grow freely while it stays open. Close: pin to the current height, then animate → 0.
    async void Animate(Border host, Control content, bool open, Tok tok)
    {
        var id = ++tok.V;
        if (open)
        {
            var w = host.Bounds.Width;
            if (w < 1) w = Bounds.Width;
            if (w < 1) w = 400;
            content.Measure(new Size(w, double.PositiveInfinity));
            var h = content.DesiredSize.Height;
            host.MaxHeight = h; // animates from the current (0) value
            await Task.Delay(220);
            if (tok.V == id) WithoutTransitions(host, () => host.MaxHeight = double.PositiveInfinity);
        }
        else
        {
            var cur = host.Bounds.Height;
            if (double.IsFinite(cur) && cur > 0) WithoutTransitions(host, () => host.MaxHeight = cur);
            Dispatcher.UIThread.Post(() => { if (tok.V == id) host.MaxHeight = 0; }, DispatcherPriority.Render);
        }
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

        // One row: left text and a right-aligned text (either may be blank).
        void AddRow(string left, IBrush leftBrush, string right, IBrush rightBrush)
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
            if (left.Length > 0) row.Children.Add(Mono(left, leftBrush));
            DetailPanel.Children.Add(row);
        }

        // Line 1: our address(es) on the left, all peers' allowed IPs on the right.
        var ourIps = string.Join(", ", _vm.AddressValues);
        var allowed = string.Join(", ", _vm.Peers.SelectMany(p => p.AllowedIpValues).Distinct());
        if (ourIps.Length > 0 || allowed.Length > 0) AddRow(ourIps, Syntax.IpBrush, allowed, Syntax.IpBrush);

        // Then one line per peer that defines a DNS server and/or domains:
        //   DNS server on the left, the domains it resolves right-aligned.
        foreach (var p in _vm.Peers)
        {
            var domains = string.Join(", ", p.DomainValues);
            if (!p.HasDns && domains.Length == 0) continue;
            AddRow(p.HasDns ? p.Dns.Trim() : "", Syntax.IpBrush, domains, Syntax.DomainBrush);
        }

        if (DetailPanel.Children.Count == 0)
            AddRow("no split DNS configured", Syntax.IpBrush, "", Syntax.IpBrush);
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
