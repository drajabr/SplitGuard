# SplitGuard 0.6 — UI program

Goal: the approved "one design, two screens" master–detail system (see the design
proposal artifact), fully polished as **0.6.1**, with UI/UX parity between Windows and
Android guaranteed by construction: one component set, one breakpoint.

## Principles (locked)

- Status first: the glance answers "am I protected?"
- Full-screen editing on narrow; detail pane on wide. No accordion cramming.
- Same five components everywhere: **StatusHero, TunnelRow, Pill, FieldGroup, ActionBar**.
- Semantics carry over unchanged: accent = value, amber = contested, ● = current
  holder, pin = device DNS.
- Motion: structural transitions animate on both heads (paced compositor); short (~200ms),
  never blocking input.

## Phases

1. **Toolchain groundwork — Avalonia 12.1** *(blocked on SDK)*
   12.1's source generators need Roslyn ≥ 4.14: install .NET 10 SDK locally + in CI
   (`setup-dotnet`), reinstall the Android workload, retarget `net8.0-android34.0` →
   `net10.0-android`, re-verify AOT + signing + libwg-go packaging. Do this FIRST so the
   new UI is built once, on the final framework.
2. **Components** — `Views/Controls/`: StatusHero (dot + name + rates + 3 counters),
   TunnelRow (dot, name, one-line stats, switch, chevron), ActionBar. Drop into the
   existing shell behind a feature flag; ship as 0.6.0-alpha for feel-testing.
3. **Navigation** — page stack for Compact (Home → TunnelPage → PeerPage → SettingsPage
   → Export/Pair/Scan pages, hardware back support), master–detail rail ≥ ~700px.
   Cards stop resizing the list; editing becomes one-peer-per-page with grouped sections.
4. **Desktop rail** — sidebar (hero + rows + quick actions) + detail pane; the narrow
   window IS the mobile layout (one breakpoint, no platform checks in XAML).
5. **Polish pass → 0.6.1** — motion audit, both themes × both accents × 4 zoom steps,
   empty states, keyboard nav (desktop) + touch targets ≥ 44dp (Android), demo-harness
   screens re-scripted, screenshot suite for both heads.

## Parity checklist (every screen, both heads)

- [ ] Same component tree (no per-platform layout forks beyond the breakpoint)
- [ ] Same copy, same iconography (symbols font subset covers all glyphs)
- [ ] Contested/holder marking identical
- [ ] Reachable by keyboard (Win) and by thumb (Android)
- [ ] Verified via --ui-demo (Win) and emulator (Android) screenshots
