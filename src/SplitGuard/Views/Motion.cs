using System;
using System.Diagnostics;
using Avalonia.Animation.Easings;
using Avalonia.Threading;

namespace SplitGuard.Views;

// One source of truth for motion. Durations are tiered by SPATIAL SCALE, not per site:
//   Fast — in-place feedback on a single control (hover / press / focus / selected color and
//          opacity fades); nothing moves or resizes.
//   Base — one control moving or resizing on its own (the pill-toggle knob slide + track fade,
//          the card's edit-border color).
//   Slow — a whole surface/region growing, collapsing, revealing or scrolling (card + settings
//          panel expand/collapse, pane swaps, reveals, the status bar). Co-running motions from
//          one trigger MUST share this token so the composite reads as a single event.
// One curve — CubicEaseOut — everywhere, shared by XAML transitions (Easing="{x:Static
// v:Motion.Standard}") and the code tween (Motion.Tween), so the two can never drift apart.
public static class Motion
{
    public const int FastMs = 120, BaseMs = 160, SlowMs = 200, CushionMs = 40;
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(FastMs);
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(BaseMs);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(SlowMs);

    // The shared easing instance (stateless, so safe to share between XAML and code).
    public static readonly Easing Standard = new CubicEaseOut();

    // Shared reveal slide-in distance (px) for fade+slide entrances.
    public const double RevealShift = 8;

    // The single code tween: a DispatcherTimer that steps `apply` along Standard from -> to over
    // `ms`. Three details are load-bearing and must not change:
    //  - 15ms interval at DispatcherPriority.Render, so ticks don't starve behind the heavy
    //    relayout a collapse triggers (default priority made the motion snap).
    //  - the <0.5 short-circuit applies the target and fires done() synchronously for a zero-delta
    //    move (generation-guarded callers rely on done() firing).
    //  - it writes plain values (never an Animation with FillMode.Forward, which keeps clamping
    //    the property after completion and froze/clipped the expand).
    // Callers keep their own generation guard to cancel a stale finalize on rapid re-toggle.
    public static void Tween(double from, double to, int ms, Action<double> apply, Action? done = null)
    {
        if (Math.Abs(to - from) < 0.5) { apply(to); done?.Invoke(); return; }
        var sw = Stopwatch.StartNew();
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            var p = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            apply(from + (to - from) * Standard.Ease(p));
            if (p >= 1) { timer!.Stop(); done?.Invoke(); }
        });
        timer.Start();
    }
}
