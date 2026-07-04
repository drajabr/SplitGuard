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
    }

    // Syntax-colored collapsed detail as atomic tokens in a WrapPanel: addresses in
    // IP color, domains in domain color. Each token is a whole TextBlock, so wrapping
    // moves a full address/domain to the next line and never splits one.
    void BuildDetail()
    {
        DetailPanel.Children.Clear();
        if (_vm is null) return;

        void AddToken(string text, IBrush brush)
        {
            // Use the "mono" style class so FontSize follows the zoom-scaled Fs12 resource.
            var tb = new TextBlock { Text = text, Foreground = brush, Margin = new Avalonia.Thickness(0, 0, 10, 2) };
            tb.Classes.Add("mono");
            DetailPanel.Children.Add(tb);
        }

        foreach (var addr in _vm.AddressValues) AddToken(addr, Syntax.IpBrush);
        foreach (var domain in _vm.AllDomains) AddToken(domain, Syntax.DomainBrush);
        if (DetailPanel.Children.Count == 0)
            AddToken("no split DNS configured", Syntax.IpBrush);
    }

    // Clicking blank card space toggles: collapsed → edit, editing → cancel/collapse.
    // Clicks anywhere inside the expanded body never collapse the card.
    void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TunnelViewModel vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var element = e.Source as Avalonia.Visual;
        while (element is not null && element != this)
        {
            if (element is Button or ToggleButton or ToggleSwitch or TextBox or ComboBox) return;
            if (element == ExpandHost) return;
            element = element.GetVisualParent();
        }
        if (vm.IsEditing) vm.CancelEditCommand.Execute(null);
        else vm.BeginEditCommand.Execute(null);
    }
}
