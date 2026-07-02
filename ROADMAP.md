# WG Split DNS — Roadmap

Status: **design complete, implementation not started.** Docs are disjoint by role: this file = why + scope + milestones · [SPEC.md](SPEC.md) = every technical decision (the build blueprint) · [AGENTS.md](AGENTS.md) = rules and invariants.

## Purpose

A small native Windows utility with one job: **split DNS for WireGuard**. Connect one or more tunnels and decide, per peer, which DNS server resolves which domains — optionally pinning one peer's DNS as the device-wide resolver with automatic failover — while everything not listed keeps using normal system DNS, untouched. Embeds its own WireGuard client (WireGuardNT), so users need nothing else installed.

## Non-goals

No DNS blocking, no local DNS forwarder, no general VPN-manager features, no per-app routing, no MSIX, no tray daemon or Windows service in v1. It is a configuration utility: rules persist after close; event-driven lifecycle runs only while the app is open.

## Key decision records

- **NRPT over a local forwarder**: NRPT is the OS-native per-domain DNS table, honored by everything using the Windows resolver, and **fail-open** (app dies ⇒ stock DNS). A 127.0.0.1:53 forwarder fails closed, must own port 53, requires overwriting adapter DNS, and forces a resident service. Rejected. Caveats (nslookup/DoH bypass, GPO override) are mitigated per SPEC.
- **Avalonia over WinUI 3**: originally WinUI 3; switched because the agreed dense card UI needs pervasive density overrides there and Windows App SDK adds build/deploy fragility. Avalonia keeps the identical C#/.NET 8 backend, builds to a single self-contained exe with plain `dotnet publish`, and matches the approved mock with a small style set. Trade-off accepted: Fluent-styled drawn controls, not OS-native ones.
- **Embedded client via official signed `wireguard.dll`** (fetched at build, never committed) rather than requiring the official app; external (official-client) tunnels are still detected and usable read-only.
- **Per-peer DNS/domains** (not per-tunnel): a peer is a remote network; its DNS lives there. Multiple "endpoints" are modeled correctly as multiple peers. One global pin for device-wide DNS.

## Deliverables

`README.md` · `AGENTS.md` · `SPEC.md` · `build.ps1` (only .NET 8 SDK required) · `.github/workflows/build-release.yml` (CI on push/PR; Release on `v*` tags) · `src/WgSplitDns/` (Avalonia app: Models, Services, ViewModels, Views — full layout in SPEC).

## Milestones

- **M0 — Foundation** ✅ docs + skeleton
- **M1 — Build pipeline**: csproj, manifest, empty window, `build.ps1` incl. `wireguard.dll` fetch, CI green
- **M2 — Core**: models, `.conf` parser, DPAPI persistence, RuleStore
- **M3 — WireGuard engine**: TunnelManager (adapter, config, routes, stats)
- **M4 — NRPT engine**: tagged rules, catch-all + chain, reconciliation, GPO detection
- **M5 — UI**: window, cards, peer blocks, view/edit, pin, live domain cards, import
- **M6 — Test bar + external tunnels**
- **M7 — Release v0.1.0**

Each milestone lands in a working state (app builds and runs at every step).
