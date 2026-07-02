# AGENTS.md — WG Split DNS

Rules for AI agents and contributors. [SPEC.md](SPEC.md) holds every technical decision — implement from it, don't research alternatives. [ROADMAP.md](ROADMAP.md) holds scope and milestone order. Do not silently deviate from any of the three.

## Status

Implemented through M6; compiles clean and packages via `build.ps1`. Driver/NRPT behavior not yet exercised on a live tunnel — treat runtime paths as unverified until M7's on-machine check.

## Boundaries

One window, plain MVVM (hand-rolled `INotifyPropertyChanged`, no framework). Services are the only code with side effects:

| Service | Owns | Must never |
|---|---|---|
| `TunnelManager` | Our tunnels via `wireguard.dll`: adapter, config, routes, stats | Touch NRPT; touch adapters it didn't create |
| `NrptService` | All NRPT reads/writes, catch-all chain, reconciliation, GPO detection, cache flush | Touch untagged rules; set adapter DNS; leave a dead catch-all |
| `RuleStore` | config.json + DPAPI for secrets | Persist or log secrets in plaintext |
| `TunnelService` | External-adapter detection, suspend/resume of their rules | Modify external tunnels |
| `TestService` | Test-bar resolution | Touch system state |

UI never calls Win32/CIM directly.

## Hard invariants — breaking any is a bug, full stop

1. **Fail-open DNS.** No state may leave the machine without DNS if the app crashes. Never set adapter DNS, never bind port 53, catch-all always has ≥1 live server or doesn't exist.
2. **Only rules tagged `WG-SPLIT-DNS`** are ever created/modified/deleted. All other NRPT rules are read-only.
3. **Unlisted domains are never affected.** No pin ⇒ no catch-all. Removing all tagged rules restores 100% stock behavior.
4. **Per-domain rules out-rank the catch-all** via NRPT longest-suffix matching — relied upon; never "fix" or reimplement.
5. **Pinned tunnel down ⇒ catch-all rewritten immediately** (next live server) or removed; chain's system-DNS tail refreshed on network changes.
6. **Secrets protected**: DPAPI at rest, masked in UI, never in logs/exceptions, exported only via explicit `.conf` export.
7. **Reconcile at startup** (no invisible orphaned rules after a crash); GPO NRPT present ⇒ visible warning, never a silent no-op.
8. **Flush resolver cache after every NRPT mutation.**
9. **Imported `DNS=` lines are per-peer suggestions** — never applied as global/adapter DNS.
10. **No committed binaries** — `wireguard.dll` is fetched at build time, official signed only.

## UI rules

Avalonia 11 Fluent, stock controls + the single shared style set in `Views/Styles.axaml` (dense 28px inputs, 4px corners, item cards, dashed add) — no per-view ad-hoc styling, no pills, sentence case, verb-first buttons, no exclamation marks. Interaction spec (view/edit gating, pin arbitration, live domain cards, validation) is in SPEC.md "UI" and is owner-approved — pixel decisions there are final.

## Verify

- Build: `.\build.ps1` (needs .NET 8 SDK only). Dev loop: `dotnet build src/WgSplitDns`. App requires elevation (manifest → UAC).
- NRPT state: `Get-DnsClientNrptRule` (look for the tag). Resolution: in-app test bar or `Resolve-DnsName` — **never `nslookup`** (it bypasses NRPT).
- CI: push/PR builds; tag `v*` releases.

## Definition of done (any change)

1. `.\build.ps1` clean.
2. Invariants hold — especially: kill mid-operation, relaunch, reconciliation leaves no orphans; unpin + disconnect-all ⇒ zero tagged rules.
3. No secrets in logs or unmasked UI.
4. UI within the shared-style rule and SPEC interaction spec.
5. Docs updated if behavior/scope moved.

## Release

Bump version in csproj → tag `vX.Y.Z` → push tag → Actions attaches zips to a GitHub Release. Notes: user-facing changes only, one line each.
