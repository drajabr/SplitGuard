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
    int _revealGen;

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
            _revealGen++;
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

    // The peer body runs its own clipped height curtain, symmetric both ways: expand grows its
    // Height 0 -> content (fading/sliding the content in), collapse shrinks content -> 0 (content
    // clipped away as it closes) then hides. The outer tunnel card's body is auto-height, so it
    // tracks the peer frame by frame; suppression stops it starting a competing follow tween
    // (which is what made collapse blank-then-snap instead of animating).
    void AnimateReveal(bool expand)
    {
        var gen = ++_revealGen;
        var card = this.FindAncestorOfType<TunnelCard>();
        card?.BeginBodySuppression();
        // Unconditional: every started tween fires done() exactly once (Motion.Tween guarantees
        // it, even on a zero-delta short-circuit), so decrementing here — regardless of whether
        // this generation is still current — keeps the suppression counter balanced when a rapid
        // re-toggle supersedes an in-flight reveal. The final-state restore stays gen-guarded.
        void Release() => card?.EndBodySuppression();

        if (expand)
        {
            // Compute the TRUE target height before animating, by forcing a real layout pass with
            // the body at auto height — not a manual Measure at a guessed width. A manual measure
            // could mis-wrap a row or miss the trailing "Remove peer" button (the glitch, which
            // only showed when peers differed in height); reading the arranged Bounds.Height after
            // a layout pass is exactly what the final layout will be, so there's no end-snap.
            PeerBody.Opacity = 0;              // don't flash full-height content during the pass
            PeerBody.IsVisible = true;
            PeerBody.Height = double.NaN;       // auto -> real natural height at the real width
            PeerBody.UpdateLayout();
            var to = PeerBody.Bounds.Height;
            if (to < 1)                          // layout not settled yet — fall back to a measure
            {
                var w = (PeerBody.Parent as Control)?.Bounds.Width ?? this.Bounds.Width;
                if (w < 1) w = 360;
                PeerBody.Measure(new Size(w, double.PositiveInfinity));
                to = PeerBody.DesiredSize.Height;
            }
            PeerBody.Height = 0;
            _shift.Y = RevealShift;
            TunnelCard.Tween(0, to, RevealMs,
                v =>
                {
                    if (_revealGen != gen) return;
                    PeerBody.Height = v;
                    var f = to > 0.5 ? v / to : 1;
                    PeerBody.Opacity = f;
                    _shift.Y = RevealShift * (1 - f);
                },
                () => { if (_revealGen == gen) { PeerBody.Height = double.NaN; PeerBody.Opacity = 1; _shift.Y = 0; } Release(); });
        }
        else
        {
            var from = PeerBody.Bounds.Height;
            PeerBody.Height = from;
            _shift.Y = 0;
            TunnelCard.Tween(from, 0, RevealMs,
                v => { if (_revealGen == gen) { PeerBody.Height = v; PeerBody.Opacity = from > 0.5 ? v / from : 0; } },
                () => { if (_revealGen == gen) { PeerBody.IsVisible = false; PeerBody.Height = 0; } Release(); });
        }
    }

    // Metric committed: resolve any duplicate within the peer's route group.
    void OnMetricCommitted(object? sender, RoutedEventArgs e) =>
        (DataContext as PeerViewModel)?.CommitMetric();
}
