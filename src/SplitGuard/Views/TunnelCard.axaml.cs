using System.ComponentModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class TunnelCard : UserControl
{
    // wg-quick highlighting sharing the fields-mode palette: property names in the
    // accent color, values typed (keys purple, IPs blue, masks/ports amber, domains green).
    static string BuildXshd(string prop, string section, string key, string ip, string num, string domain) => $$"""
        <SyntaxDefinition name="WgConf" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="Section" foreground="{{section}}" fontWeight="bold" />
          <Color name="Prop" foreground="{{prop}}" />
          <Color name="Key64" foreground="{{key}}" />
          <Color name="Ip" foreground="{{ip}}" />
          <Color name="Num" foreground="{{num}}" />
          <Color name="Domain" foreground="{{domain}}" />
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
        """;

    static IHighlightingDefinition _confHighlighting =
        LoadHighlighting(BuildXshd("#3378DD", "#C77E16", "#9A6FD0", "#4098D7", "#C77E16", "#58A65C"));

    static event Action? HighlightingChanged;

    public static void UpdateHighlighting(string prop, string section, string key, string ip, string num, string domain)
    {
        _confHighlighting = LoadHighlighting(BuildXshd(prop, section, key, ip, num, domain));
        HighlightingChanged?.Invoke();
    }

    static IHighlightingDefinition LoadHighlighting(string xshd)
    {
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    TunnelViewModel? _vm;
    bool _syncingEditor;

    readonly Action _onHighlightingChanged;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HighlightingChanged += _onHighlightingChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HighlightingChanged -= _onHighlightingChanged;
    }

    public TunnelCard()
    {
        InitializeComponent();
        _onHighlightingChanged = () => ConfEditor.SyntaxHighlighting = _confHighlighting;
        ConfEditor.SyntaxHighlighting = _confHighlighting;
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
            // Always push on mode entry; otherwise only when the texts diverge.
            if (e.PropertyName == nameof(TunnelViewModel.ConfigText) && ConfEditor.Text == _vm.ConfigText) return;
            _syncingEditor = true;
            ConfEditor.Text = _vm.ConfigText;
            _syncingEditor = false;
        }
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
