using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using SplitGuard.ViewModels;

namespace SplitGuard.Views;

public partial class PeerBlock : UserControl
{
    const int RevealMs = Motion.SlowMs;            // structural reveal (shared token)
    const double RevealShift = Motion.RevealShift;

    PeerViewModel? _vm;
    readonly TranslateTransform _shift = new(0, 0);

    public PeerBlock()
    {
        InitializeComponent();
        PeerBody.RenderTransform = _shift;
        PeerBody.ClipToBounds = true; // clip its own animated-height curtain

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as PeerViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;

            // Initial state, no animation (matches the card's own init).
            var exp = _vm?.IsExpanded ?? false;
            PeerBody.IsVisible = exp;
            PeerBody.Opacity = exp ? 1 : 0;
            PeerBody.Height = exp ? double.NaN : 0;
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

    // The peer body runs its own clipped height curtain on the "peer" channel, symmetric both
    // ways: expand grows its Height toward the content (fading/sliding the content in), collapse
    // shrinks toward 0 (content clipped away as it closes) then hides. The outer tunnel card's
    // body is auto-height and simply follows the peer frame by frame (the card's size-follow
    // handler stands down while anything animates inside it — Motion.IsAnimating). A re-toggle
    // mid-flight settles the previous run and continues from the CURRENT height, so rapid
    // open/close never jumps.
    void AnimateReveal(bool expand)
    {
        if (expand)
        {
            // Compute the TRUE target height before animating, by forcing a real layout pass
            // with the body at auto height — not a manual Measure at a guessed width. A manual
            // measure could mis-wrap a row or miss the trailing "Remove peer" button; reading
            // the arranged Bounds.Height after a layout pass is exactly what the final layout
            // will be, so there's no end-snap. (No render happens between these writes.)
            var from = PeerBody.IsVisible ? PeerBody.Bounds.Height : 0; // continue an interrupted close
            if (!PeerBody.IsVisible) PeerBody.Opacity = 0; // don't flash content during the pass
            PeerBody.IsVisible = true;
            PeerBody.Height = double.NaN;
            PeerBody.UpdateLayout();
            var to = PeerBody.Bounds.Height;
            if (to < 1)                          // layout not settled yet — fall back to a measure
            {
                var w = (PeerBody.Parent as Control)?.Bounds.Width ?? this.Bounds.Width;
                if (w < 1) w = 360;
                to = Motion.MeasureHeight(PeerBody, w);
            }
            PeerBody.Height = from;
            Motion.Animate(this, "peer", from, to, RevealMs,
                v =>
                {
                    PeerBody.Height = v;
                    var f = to > 0.5 ? v / to : 1;
                    PeerBody.Opacity = f;
                    _shift.Y = RevealShift * (1 - f);
                },
                interrupted =>
                {
                    if (interrupted) return; // the successor run owns the curtain now
                    PeerBody.Height = double.NaN;
                    PeerBody.Opacity = 1;
                    _shift.Y = 0;
                });
        }
        else
        {
            var from = PeerBody.Bounds.Height;
            PeerBody.Height = from;
            _shift.Y = 0;
            Motion.Animate(this, "peer", from, 0, RevealMs,
                v => { PeerBody.Height = v; PeerBody.Opacity = from > 0.5 ? v / from : 0; },
                interrupted =>
                {
                    if (interrupted) return;
                    PeerBody.IsVisible = false;
                    PeerBody.Height = 0;
                });
        }
    }

    // Metric committed: resolve any duplicate within the peer's route group.
    void OnMetricCommitted(object? sender, RoutedEventArgs e) =>
        (DataContext as PeerViewModel)?.CommitMetric();
}
