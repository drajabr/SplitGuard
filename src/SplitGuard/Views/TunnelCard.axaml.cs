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
    // Minimal wg-quick highlighting: comments, [sections], keys. Mid-tone colors
    // chosen to stay readable in both light and dark themes.
    const string ConfXshd = """
        <SyntaxDefinition name="WgConf" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="Section" foreground="#C77E16" fontWeight="bold" />
          <Color name="Key" foreground="#3E8ED0" />
          <RuleSet>
            <Span color="Comment" begin="#" />
            <Rule color="Section">\[[^\]]*\]</Rule>
            <Rule color="Key">^\s*[A-Za-z]+(?=\s*=)</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;

    static readonly IHighlightingDefinition ConfHighlighting = LoadHighlighting();

    static IHighlightingDefinition LoadHighlighting()
    {
        using var reader = XmlReader.Create(new StringReader(ConfXshd));
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
