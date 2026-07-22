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
- **No hardcoded pixel thresholds.** Every layout decision measures whether the CONTENT
  actually fits: measure the candidates' natural (desired) sizes, compare against the
  available space, switch layouts only when something would truncate or clip, and add a
  small hysteresis so borderline sizes don't oscillate. This keeps zoom steps, font
  choices, and display scaling safe automatically. `MainView.UpdateSettingsLayout` is the
  reference implementation of the pattern; anything written as `width < NNN` is a defect.

## Phases

Tracked here, in the repo — tick items as they land; each phase ends with a commit that
updates this file.

### Phase 1 — toolchain groundwork: Avalonia 12.1 *(blocked on SDK)*
12.1's source generators need Roslyn ≥ 4.14 (the .NET 8 SDK ships 4.11 and silently
skips them — verified 2026-07-23). Do this FIRST so the new UI is built once, on the
final framework.
- [ ] .NET 10 SDK installed locally (`~/.dotnet`) and in CI (`setup-dotnet`)
- [ ] Android workload reinstalled under the new SDK band
- [ ] Retarget `net8.0-android34.0` → `net10.0-android`; desktop TFMs re-verified
- [ ] Avalonia 12.1 + AvaloniaEdit 12.0 bumped; obsolete-API removals fixed
      (`DragEventArgs.Data` → DataTransfer, `IClipboard.GetTextAsync`, `Bitmap.Save`)
- [ ] Release APK: full AOT, signing, patched libwg-go packaging re-verified on emulator
- [ ] Both heads screenshot-verified; release as 0.5.16 (same UI, new framework)

### Phase 2 — components (`Views/Controls/`)
- [ ] StatusHero: dot + tunnel + rates + domains/routes/shared counters
- [ ] TunnelRow: dot, name, one-line stats, switch, chevron
- [ ] ActionBar: bottom bar (narrow) / pane header (wide) with the same actions
- [ ] Integrated into the existing shell behind a feature flag; ship 0.6.0-alpha

### Phase 3 — navigation (narrow layout)
- [ ] Page stack: Home → TunnelPage → PeerPage → SettingsPage → Export/Pair/Scan
- [ ] Android hardware back pops the stack
- [ ] One-peer-per-page editor with grouped sections replaces the accordion
- [ ] Cards stop resizing the list (no more whole-list relayout on expand)

### Phase 4 — wide layout: master–detail rail
- [ ] Sidebar (hero + rows + quick actions) + detail pane
- [ ] **Content-measured switch, not a pixel breakpoint**: the rail shows only when the
      sidebar's natural minimum width AND the detail pane's natural minimum width both
      fit the window at the current zoom/font (measure desired sizes, hysteresis on the
      boundary — the UpdateSettingsLayout pattern). Narrow = the phone layout, by
      construction.
- [ ] No platform checks in layout XAML — width fit is the only discriminator

### Phase 5 — polish pass → 0.6.1
- [ ] Retrofit every remaining hardcoded threshold to measured rules: QR overlay
      stack/side-by-side (today `w < 640`), stacked QR cap (440), scan-stage height
      clamps (240–380/420), and any `MinWidth`/`MaxWidth` that exists to prevent clipping
- [ ] Motion audit (both heads), themes × accents × zoom matrix
- [ ] Empty states; keyboard nav (desktop); touch targets ≥ 44dp (Android)
- [ ] Demo harness re-scripted for the new screens; screenshot suites both heads
- [ ] Parity checklist below green on every screen; release 0.6.1

## Parity checklist (every screen, both heads)

- [ ] Same component tree (no per-platform layout forks beyond the breakpoint)
- [ ] Same copy, same iconography (symbols font subset covers all glyphs)
- [ ] Contested/holder marking identical
- [ ] Reachable by keyboard (Win) and by thumb (Android)
- [ ] Verified via --ui-demo (Win) and emulator (Android) screenshots
