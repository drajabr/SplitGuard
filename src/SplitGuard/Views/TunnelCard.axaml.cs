using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class TunnelCard : UserControl
{
    public TunnelCard()
    {
        InitializeComponent();
        // External cards have no interface column: let the peers section span the full width.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TunnelViewModel { IsExternal: true })
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
        AddHandler(Gestures.TappedEvent, OnTapped);
    }

    // Clicking a collapsed card (outside interactive controls) opens edit mode.
    void OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not TunnelViewModel vm || vm.IsEditing) return;
        var element = e.Source as Avalonia.Visual;
        while (element is not null && element != this)
        {
            if (element is Button or ToggleButton or ToggleSwitch or TextBox or ComboBox) return;
            element = element.GetVisualParent();
        }
        vm.BeginEditCommand.Execute(null);
    }
}
