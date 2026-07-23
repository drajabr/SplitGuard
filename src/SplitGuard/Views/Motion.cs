using System;
using System.Diagnostics;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
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

    // The single code tween: steps `apply` along Standard from -> to over `ms`, paced by the
    // COMPOSITOR (TopLevel.RequestAnimationFrame): exactly one step per presented frame,
    // vsync-aligned, on every head. A wall-clock DispatcherTimer can never do this — its ticks
    // beat against the ~16.7ms frame clock (some frames get two values, some none), which is
    // what made phone animations feel low-fps even when every tick landed. The old Compact
    // 30ms tick (~33fps cap) is gone for the same reason: when a step's relayout runs long,
    // RAF self-throttles to the achievable rate without losing vsync alignment, so no manual
    // cap is needed. Two details remain load-bearing:
    //  - the <0.5 short-circuit applies the target and fires done() synchronously for a
    //    zero-delta move (generation-guarded callers rely on done() firing).
    //  - it writes plain values (never an Animation with FillMode.Forward, which keeps
    //    clamping the property after completion and froze/clipped the expand).
    // Callers keep their own generation guard to cancel a stale finalize on rapid re-toggle.
    // ALL live tweens step from ONE pump, so co-running motions from a single trigger
    // (height + reveal + scroll) apply inside one frame and resolve in one layout pass.
    sealed class ActiveTween
    {
        public required Stopwatch Sw;
        public required double From, To;
        public required int Ms;
        public required Action<double> Apply;
        public Action? Done;
    }

    static readonly List<ActiveTween> _tweens = new();
    static TopLevel? _frameHost;
    static bool _framePending;
    static DispatcherTimer? _fallbackPump; // pre-attach only (a tween before the view lands)

    // MainView hands us its TopLevel when it enters a visual tree (the one shared code
    // path both heads pass through), and clears it on detach.
    public static void AttachFrameHost(TopLevel? top)
    {
        _frameHost = top;
        // A frame request pending on the OLD host may never fire once it's gone; without
        // this reset its guard would block the new host's first Kick forever.
        _framePending = false;
        if (top is not null) _fallbackPump?.Stop();
        if (_tweens.Count > 0) Kick(); // don't strand live tweens across a host swap
    }

    public static void Tween(double from, double to, int ms, Action<double> apply, Action? done = null)
    {
        if (Math.Abs(to - from) < 0.5) { apply(to); done?.Invoke(); return; }
        _tweens.Add(new ActiveTween { Sw = Stopwatch.StartNew(), From = from, To = to, Ms = ms, Apply = apply, Done = done });
        Kick();
    }

    static void Kick()
    {
        if (_frameHost is { } top)
        {
            if (_framePending) return; // one outstanding frame request at a time
            _framePending = true;
            top.RequestAnimationFrame(_ =>
            {
                // Clear BEFORE pumping: apply/done may start tweens re-entrantly, and their
                // Kick must be able to schedule the next frame.
                _framePending = false;
                Pump();
                if (_tweens.Count > 0) Kick();
            });
        }
        else
        {
            _fallbackPump ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Pump());
            if (!_fallbackPump.IsEnabled) _fallbackPump.Start();
        }
    }

    static void Pump()
    {
#if DEBUG
        // Frame-pacing probe: `adb logcat -s mono-stdout` (or console on desktop) shows the
        // real step intervals — how the 33fps-timer jank was verified fixed. Debug-only.
        Console.WriteLine($"[motion] {Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:0.0}ms tweens={_tweens.Count}");
#endif
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
        if (_tweens.Count == 0) _fallbackPump?.Stop();
    }
}
