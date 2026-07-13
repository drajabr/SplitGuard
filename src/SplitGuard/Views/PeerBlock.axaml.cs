using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class PeerBlock : UserControl
{
    const int RevealMs = 200;      // match TunnelCard.AnimMs
    const double RevealShift = 8;

    PeerViewModel? _vm;
    readonly TranslateTransform _shift = new(0, 0);
    int _revealGen;

    public PeerBlock()
    {
        InitializeComponent();
        PeerBody.RenderTransform = _shift;

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as PeerViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;

            // Initial state, no animation (matches the card's own init).
            _revealGen++;
            var exp = _vm?.IsExpanded ?? false;
            PeerBody.IsVisible = exp;
            PeerBody.Opacity = exp ? 1 : 0;
            _shift.Y = 0;
        };

        // Clicking the header's empty space toggles the peer body, like the tunnel card;
        // the inline inputs (name, endpoint, metric) keep the pointer to themselves.
        HeaderRow.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (DataContext is not PeerViewModel vm) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            for (var el = e.Source as Avalonia.Visual; el is not null && el != HeaderRow; el = el.GetVisualParent())
                if (el is Button or TextBox) return;
            vm.IsExpanded = !vm.IsExpanded;
            e.Handled = true;
        });
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PeerViewModel.IsExpanded) && _vm is not null)
            AnimateReveal(_vm.IsExpanded);
    }

    // The OUTER tunnel card owns the height motion (it animates via ExpandContent.SizeChanged
    // once IsVisible flips). Expand: flip IsVisible so the card grows, and fade/slide the body
    // in over the same window. Collapse: flip IsVisible off immediately so the card shrinks at
    // the exact same rate it grew — no separate fade-out pass (that made collapse feel ~2x
    // slower than expand).
    void AnimateReveal(bool expand)
    {
        var gen = ++_revealGen;
        if (expand)
        {
            PeerBody.IsVisible = true;
            PeerBody.Opacity = 0;
            _shift.Y = RevealShift;
            TunnelCard.Tween(0, 1, RevealMs, v =>
            {
                if (_revealGen != gen) return;
                PeerBody.Opacity = v;
                _shift.Y = RevealShift * (1 - v);
            });
        }
        else
        {
            PeerBody.Opacity = 0;
            _shift.Y = 0;
            PeerBody.IsVisible = false; // card shrinks now, same 200ms as the grow
        }
    }

    // Metric committed: resolve any duplicate within the peer's route group.
    void OnMetricCommitted(object? sender, RoutedEventArgs e) =>
        (DataContext as PeerViewModel)?.CommitMetric();
}
