using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SplitGuard.Views;

// One source of truth for motion. Durations are tiered by SPATIAL SCALE, not per site:
//   Fast — in-place feedback on a single control (hover / press / focus / selected color and
//          opacity fades); nothing moves or resizes.
//   Base — one control moving or resizing on its own (the pill-toggle knob slide + track fade,
//          the card's edit-border color).
//   Slow — a whole surface/region growing, collapsing, revealing or scrolling (card + settings
//          panel expand/collapse, pane swaps, reveals). Co-running motions from one trigger
//          MUST share this token so the composite reads as a single event.
// One curve — CubicEaseOut — everywhere, shared by XAML transitions (Easing="{x:Static
// v:Motion.Standard}") and the channel engine below, so the two can never drift apart.
//
// THE CHANNEL ENGINE. All code-driven animation goes through Animate(host, channel, ...):
//  - One writer per (host, channel), ever. Starting an animation on a busy channel SETTLES the
//    old run first — its done(interrupted: true) fires so it can leave coherent state — and the
//    new run begins from wherever the value currently is. This replaces every hand-rolled
//    generation counter.
//  - Live retargeting: the target is a Func<double>, sampled every frame. If content changes
//    mid-flight the SAME animation bends toward the new target (rebased from the current value
//    with a fresh easing run) instead of a second animation fighting the first.
//  - One shared frame clock: a single 15ms DispatcherTimer at Render priority drives every
//    active animation (ticks don't starve behind the heavy relayout a collapse triggers).
//  - It writes plain values (never an Animation with FillMode.Forward, which keeps clamping
//    the property after completion and froze/clipped the expand).
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

    sealed class Anim
    {
        public required object Host;
        public required string Channel;
        public required Func<double> Target;
        public required Action<double> Apply;
        public Action<bool>? Done; // arg = interrupted (a successor took over the channel)
        public double From, LastTarget, Current;
        public int Ms;
        public readonly Stopwatch Clock = new();
    }

    static readonly List<Anim> _active = new();
    static DispatcherTimer? _clock;

    /// <summary>Animate a value on a keyed channel; see the class comment for the contract.</summary>
    public static void Animate(object host, string channel, double from, Func<double> target, int ms,
                               Action<double> apply, Action<bool>? done = null)
    {
        // Settle any current run on this channel: the successor owns the value from here.
        for (int i = 0; i < _active.Count; i++)
        {
            if (!ReferenceEquals(_active[i].Host, host) || _active[i].Channel != channel) continue;
            var old = _active[i];
            _active.RemoveAt(i);
            old.Done?.Invoke(true);
            break;
        }

        var to = target();
        // Zero-delta short-circuit: apply the target and complete synchronously (callers'
        // finalizers rely on done() always firing exactly once).
        if (Math.Abs(to - from) < 0.5) { apply(to); done?.Invoke(false); return; }

        var a = new Anim
        {
            Host = host, Channel = channel, Target = target, Apply = apply, Done = done,
            From = from, LastTarget = to, Current = from, Ms = ms,
        };
        a.Clock.Start();
        _active.Add(a);
        _clock ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, Tick);
        _clock.Start();
    }

    public static void Animate(object host, string channel, double from, double to, int ms,
                               Action<double> apply, Action<bool>? done = null)
        => Animate(host, channel, from, () => to, ms, apply, done);

    /// <summary>True when any channel animation runs on <paramref name="scope"/> or a visual
    /// descendant of it — e.g. "is a peer curtain running inside this card".</summary>
    public static bool IsAnimating(Visual scope)
    {
        foreach (var a in _active)
            if (a.Host is Visual v && WithinScope(v, scope)) return true;
        return false;
    }

    static bool WithinScope(Visual v, Visual scope)
    {
        for (Visual? p = v; p is not null; p = p.GetVisualParent())
            if (ReferenceEquals(p, scope)) return true;
        return false;
    }

    static void Tick(object? sender, EventArgs e)
    {
        // Snapshot: Done callbacks may start/settle other animations, mutating _active.
        var snapshot = _active.ToArray();
        List<Anim>? finished = null;
        foreach (var a in snapshot)
        {
            if (!_active.Contains(a)) continue; // settled by an earlier callback this tick

            var to = a.Target();
            if (Math.Abs(to - a.LastTarget) > 0.5)
            {
                // The content changed mid-flight: bend from the current value toward the new
                // target with a fresh easing run — continuous, no jump, no rival animation.
                a.From = a.Current;
                a.LastTarget = to;
                a.Clock.Restart();
            }

            var p = Math.Min(1, a.Clock.Elapsed.TotalMilliseconds / a.Ms);
            a.Current = a.From + (a.LastTarget - a.From) * Standard.Ease(p);
            a.Apply(a.Current);
            if (p >= 1)
            {
                _active.Remove(a);
                (finished ??= new List<Anim>()).Add(a);
            }
        }
        if (finished is not null)
            foreach (var a in finished) a.Done?.Invoke(false);
        if (_active.Count == 0) _clock!.Stop();
    }

    /// <summary>The natural height of <paramref name="content"/> at <paramref name="width"/>,
    /// measurable even while hidden (visibility is juggled without a render in between).
    /// EVERY height-animation target must come from here so wrapping is computed at the real
    /// width — a guessed width mis-wraps rows and the animation lands short, then snaps.</summary>
    public static double MeasureHeight(Control content, double width)
    {
        var wasVisible = content.IsVisible;
        if (!wasVisible) content.IsVisible = true;
        content.Measure(new Size(width, double.PositiveInfinity));
        var h = content.DesiredSize.Height;
        if (!wasVisible) content.IsVisible = false;
        return h;
    }
}
