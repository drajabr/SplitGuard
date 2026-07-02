using Avalonia.Controls;
using WgSplitDns.ViewModels;

namespace WgSplitDns.Views;

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
    }
}
