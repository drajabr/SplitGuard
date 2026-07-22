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
    // ALL live tweens step from ONE shared timer: co-running motions from a single
    // trigger (height + reveal + scroll) apply inside one dispatcher tick and resolve in
    // one layout pass, instead of three independently-phased timers beating against the
    // frame clock. Mobile (Compact) uses a 30ms tick — every Height step re-arranges the
    // whole card subtree and ~33fps of that is the smooth/janky line on a phone CPU.
    sealed class ActiveTween
    {
        public required Stopwatch Sw;
        public required double From, To;
        public required int Ms;
        public required Action<double> Apply;
        public Action? Done;
    }

    static readonly List<ActiveTween> _tweens = new();
    static DispatcherTimer? _pump;

    public static void Tween(double from, double to, int ms, Action<double> apply, Action? done = null)
    {
        // Compact (Android): structural code tweens — card/drawer heights, scroll offsets
        // — apply INSTANTLY. Every step re-measures and re-arranges whole subtrees, and
        // even one such pass per 30ms tick is the residual interaction jank on a phone
        // CPU. The render-only XAML transitions (fades, color rides, the knob slide)
        // still animate, so the UI keeps its motion language without paying layout.
        if (TunnelCard.Compact) { apply(to); done?.Invoke(); return; }
        if (Math.Abs(to - from) < 0.5) { apply(to); done?.Invoke(); return; }
        _tweens.Add(new ActiveTween { Sw = Stopwatch.StartNew(), From = from, To = to, Ms = ms, Apply = apply, Done = done });
        if (_pump is null)
        {
            var tick = TunnelCard.Compact ? 30 : 15;
            _pump = new DispatcherTimer(TimeSpan.FromMilliseconds(tick), DispatcherPriority.Render, (_, _) => Pump());
        }
        if (!_pump.IsEnabled) _pump.Start();
    }

    static void Pump()
    {
        // Snapshot: apply/done callbacks may start new tweens re-entrantly.
        var batch = _tweens.ToArray();
        foreach (var t in batch)
        {
            var p = Math.Min(1, t.Sw.Elapsed.TotalMilliseconds / t.Ms);
            t.Apply(t.From + (t.To - t.From) * Standard.Ease(p));
            if (p >= 1)
            {
                _tweens.Remove(t);
                t.Done?.Invoke();
            }
        }
        if (_tweens.Count == 0) _pump!.Stop();
    }
}
