using System.ComponentModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
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
            OnEditingChanged();

            // External cards have no interface column: let the peers section span the full width.
            if (_vm is { IsExternal: true })
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
        };
        // PointerPressed instead of Tapped: Tapped suppresses the second of two fast
        // clicks (double-tap detection), which made rapid expand/collapse feel dead.
        AddHandler(PointerPressedEvent, OnCardPressed, handledEventsToo: false);
        // Keep the active section's MaxHeight tracking its real content height so the
        // expand/collapse transition animates over the exact range (natural motion).
        LayoutUpdated += (_, _) => SyncActiveHeight();
    }

    void OnEditingChanged()
    {
        if (_vm is null) return;
        var editing = _vm.IsEditing;
        ExpandHost.Opacity = editing ? 1 : 0;
        CollapseHost.Opacity = editing ? 0 : 1;
        if (editing) CollapseHost.MaxHeight = 0; else ExpandHost.MaxHeight = 0;
        SyncActiveHeight();
    }

    void SyncActiveHeight()
    {
        if (_vm is null) return;
        if (_vm.IsEditing) SetMax(ExpandHost, ExpandContent);
        else SetMax(CollapseHost, DetailPanel);
    }

    static void SetMax(Border host, Control content)
    {
        var width = host.Bounds.Width > 0 ? host.Bounds.Width : 700;
        content.Measure(new Avalonia.Size(width, double.PositiveInfinity));
        var h = content.DesiredSize.Height;
        if (h > 0 && Math.Abs(host.MaxHeight - h) > 0.5) host.MaxHeight = h;
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
        if (e.PropertyName == nameof(TunnelViewModel.IsEditing))
            OnEditingChanged();
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
