# WG Split DNS — Roadmap

Status: **design agreed, implementation not started.** This document is the authoritative record of every decision made during design. Read [AGENTS.md](AGENTS.md) before writing any code.

## Purpose

A small, native Windows utility with one job: **split DNS for WireGuard users**. Connect one or more WireGuard tunnels and decide, per peer, which DNS server resolves which domains — including optionally making one tunnel's DNS the device-wide resolver — while everything not explicitly listed keeps using normal system DNS, untouched.

The tool embeds its own WireGuard client, so end users need nothing else installed.

## Non-goals

- Not a general VPN manager (no OpenVPN, no kill switch, no per-app routing).
- No DNS blocking / ad-blocking. Purely "which server answers for which domains".
- No local DNS forwarder/proxy (decided against — see "Why NRPT" below).
- No MSIX packaging, no tray daemon, no Windows service in v1. It is a configuration utility: rules persist after the app closes; the tunnel/NRPT lifecycle reacts to events only while the app runs.

## Stack

| Layer | Choice |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.6), C# / .NET 8 LTS, stock controls only |
| Deployment | Unpackaged, self-contained single folder (zip), x64 (arm64 optional in CI matrix) |
| Elevation | `app.manifest` → `requireAdministrator` (NRPT and the driver require admin) |
| WireGuard | Official prebuilt signed `wireguard.dll` (WireGuardNT driver), fetched at build time from download.wireguard.com — never committed to the repo |
| Split DNS | Windows NRPT via CIM (`Microsoft.Management.Infrastructure`, `root/StandardCimv2` → `MSFT_DNSClientNrptRule`) — no PowerShell process spawning |
| Persistence | JSON at `%ProgramData%\WgSplitDns\config.json`; private keys and PSKs DPAPI-encrypted (machine scope) |
| Build | `build.ps1` — requires only the .NET 8 SDK. No Visual Studio, no Go |
| CI/CD | GitHub Actions on `windows-latest`: build on push/PR; on tag `v*` create a Release with zips attached |

## Why NRPT (decision record)

- NRPT is the OS-native per-domain DNS routing table (used by DirectAccess, Always On VPN, Zscaler, Cisco). Every app using the Windows resolver honors it.
- **Fail-open**: if the app dies or rules break, queries flow to normal system DNS. A local forwarder on 127.0.0.1:53 was considered and rejected — it fails closed (app dies → whole device loses DNS), requires overwriting adapter DNS on every network (breaking our "untouched" guarantee), must own port 53, and forces a resident service.
- Known NRPT caveats, all mitigated: `nslookup` bypasses NRPT (in-app tester uses the real resolver path and is authoritative); browser DoH bypasses any local mechanism (README note); Group Policy NRPT overrides local rules entirely (app detects GPO NRPT at startup and shows a warning banner instead of failing silently).
- A forwarder engine could be added later as an opt-in advanced mode without changing the UI model. Not v1.

## Mechanism

- **Resolve rule**: NRPT rule mapping domain(s) → the owning peer's DNS server. Created when the tunnel connects, removed when it disconnects. Domain edits on a connected tunnel apply instantly.
- **Device DNS (pin)**: exactly one peer DNS may be pinned device-wide. Implemented as a single NRPT catch-all (`.`) rule carrying an ordered server chain: pinned server → other connected peers' DNS → snapshot of the physical adapters' system DNS (refreshed on network changes). Per-domain rules always out-rank the catch-all (NRPT longest-suffix matching).
- **Failover**: if the pinned peer's tunnel disconnects, the catch-all is rewritten to the next live server or removed entirely. The machine must never be left with a dead catch-all.
- All rules created by the tool carry the marker `WG-SPLIT-DNS`. The tool never touches untagged NRPT rules. Resolver cache is flushed after every change. State is reconciled against live NRPT at startup (crash recovery).
- NRPT namespace semantics: `example.com` and `.example.com` are distinct; `*.example.com` in the UI normalizes to the subdomain-inclusive form; UI offers exact vs include-subdomains behavior via the `*.` prefix convention.

## WireGuard engine

- `wireguard.dll` (WireGuardNT) creates the adapter and runs the protocol. The app implements the thin layer: config handling, interface IP assignment, routes for allowed IPs (one route per allowed-IP entry; classic `0.0.0.0/1` + `128.0.0.0/1` split for full-tunnel configs plus endpoint host route), and per-peer handshake/transfer polling (~1s).
- A tunnel's `DNS =` line from an imported `.conf` is used only as the suggested per-peer DNS value — it is **never applied as global DNS** (that is the whole point of this tool). On import it attaches to the peer whose allowed IPs contain it (first peer as fallback).
- **External tunnels**: WireGuard adapters managed by the official client are detected and listed read-only (no connect toggle); per-peer DNS/domains can still be attached to them, with automatic suspend/resume of their rules when the adapter goes down/up.
- Coexistence: same driver as the official client; no conflict.

## Data model

```json
{
  "version": 1,
  "pinnedDns": { "tunnel": "office", "peerPublicKey": "2fBt…" },
  "tunnels": [
    {
      "name": "office",
      "privateKey": "<dpapi-blob>",
      "addresses": ["10.10.0.2/32"],
      "peers": [
        {
          "publicKey": "2fBt…",
          "presharedKey": "<dpapi-blob or null>",
          "endpoint": "vpn.saba.energy:51820",
          "allowedIps": ["10.10.0.0/16"],
          "dns": "10.10.0.1",
          "domains": ["*.corp.saba.energy", "*.saba.local"]
        }
      ]
    }
  ]
}
```

Mirrors WireGuard semantics exactly: interface (name, private key, addresses) + N peers (public key, optional PSK, one endpoint each, allowed IPs) + the split-DNS layer (per-peer DNS, domains) + one global pin. "Multiple endpoints" is modeled correctly as multiple peers.

## UI specification (final, agreed through mockups)

One window, stock WinUI controls only (`Grid`, `StackPanel`, `TextBox`, `ToggleSwitch`, `ToggleButton`, `FontIcon`, `ItemsRepeater`, `ComboBox`, `ContentDialog`, `Ellipse`), 4px corners, no custom-templated controls, no pills.

### Window

- **Header**: app icon + name; "Add tunnel" button (import `.conf` file or paste config text).
- **Body**: one card per tunnel, stacked.
- **Footer**: test bar.

### Tunnel card anatomy

- **Header row**: status dot · tunnel name · (right side) `handshake Xs ago` · `↑ rate` · `↓ rate` · connect `ToggleSwitch` · pencil (edit). Handshake shows in the card header for single-peer tunnels; for multi-peer tunnels each peer block header carries its own handshake and the card header keeps aggregate ↑/↓. Stats are hidden entirely when disconnected (no dashes/zeros).
- **Interface section** (one field per line, label column left; every value rendered as a small square-corner item card):
  - `Public key` — own public key, truncated, with a **copy** affordance (copies full key, checkmark flash).
  - `Address` — one item card per address.
- **Peer blocks** — one bordered sub-block per peer:
  - Block header: "Peer" + truncated remote public key (+ lock glyph if PSK set; + per-peer handshake when multi-peer).
  - Lines: `Endpoint` (single value — one endpoint per peer, by WireGuard semantics) · `Allowed IPs` (item cards) · `DNS` (value + **pin**) · `Domains` (item cards + dashed "+ add").

### Interaction rules

- **View mode (default)**: everything read-only **except** two always-live controls: (1) domain cards — "+ add" swaps to an inline TextBox, Enter commits and applies the NRPT rule instantly, Escape cancels, × removes instantly; (2) the DNS pin. These are safe, instant, reversible operations and need no save step.
- **Pin logic**: `ToggleButton` + pin glyph on each peer's DNS line. Exactly one pinned device-wide across all tunnels/peers; pinning another moves it; clicking the active pin turns it off (= system default). Caption "device DNS" under the pinned value; "device DNS · suspended" while its tunnel is disconnected (auto-failover active, re-applies on reconnect). Pin disabled when the peer has no DNS value.
- **Edit mode (pencil)**: one card at a time; card gets accent border + "editing" label; connect toggle hidden while editing. Unlocks: name, addresses (×/add item cards), private key (masked, eye-reveal, "generate" action that updates the shown public key live), and per peer: endpoint, public key, preshared key (masked, eye-reveal, placeholder "optional"), allowed IPs (×/add), DNS value; plus "+ Add peer" and "Remove peer". Footer: `Delete tunnel` (danger, left, confirm dialog) · `Cancel` · `Save`.
- **Save semantics**: validate everything first (CIDR for addresses/allowed IPs, `host:port` endpoint, IP for DNS, base64/32-byte keys, domain syntax; duplicate domains across peers rejected) — standard WinUI error states, Save blocked while invalid. DNS/domain changes apply as instant NRPT updates; connection-field changes on a connected tunnel trigger a quick config reapply (sub-second blip, surfaced honestly).
- **Validation warning (not a block)**: peer DNS not inside that peer's allowed IPs → warn (query would leak out the default route).

### Test bar

- Row: flask icon · hostname input (flex) · server `ComboBox` (max ⅓ width) · "Test" button.
- Selector entries: `Auto (effective)` (default — resolves via the OS resolver path, honors NRPT, shows what apps actually get) · one entry per peer DNS (`office — 10.10.0.1`, `homelab (site-b) — 10.21.0.1`) which query that server directly bypassing NRPT · `System DNS`.
- Result renders as a single transient line under the bar only after a test runs: `10.10.4.20 · answered by 10.10.0.1 (office) in 12 ms`.

## Package deliverables

```
wg-split-dns/
├── README.md                      # simple: what, install, 3-step usage, how it works, caveats
├── AGENTS.md                      # agent/contributor guide — invariants and rules
├── ROADMAP.md                     # this file
├── build.ps1                      # local build: .NET 8 SDK only → dist/WgSplitDns-win-x64.zip
├── .github/workflows/
│   └── build-release.yml          # CI on push/PR; Release on v* tags
└── src/WgSplitDns/
    ├── WgSplitDns.csproj          # net8.0-windows10.0.19041.0, WindowsAppSDK, self-contained
    ├── app.manifest               # requireAdministrator
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml / .cs
    ├── Models/                    # Tunnel, Peer, AppConfig, WireGuardConfig (conf parse/serialize)
    ├── Services/                  # TunnelManager, NrptService, RuleStore, TunnelService (external), TestService
    ├── ViewModels/                # MainViewModel + per-card VMs (plain INotifyPropertyChanged)
    ├── Views/                     # card/peer templates, dialogs
    └── Assets/
```

## Milestones

- **M0 — Foundation** ✅ repo skeleton, ROADMAP.md, AGENTS.md, README.md
- **M1 — Build pipeline**: csproj, app.manifest, empty-window app, `build.ps1` (incl. wireguard.dll fetch), CI workflow building green
- **M2 — Core**: models, `.conf` parser/serializer, config persistence with DPAPI, RuleStore
- **M3 — WireGuard engine**: TunnelManager — adapter lifecycle, config apply, routes, stats polling
- **M4 — NRPT engine**: NrptService — tagged rules, catch-all + chain, reconciliation, GPO detection, cache flush
- **M5 — UI**: main window, tunnel cards, peer blocks, view/edit modes, pin, live domain cards, add-tunnel import
- **M6 — Test bar + external tunnels**: TestService, external adapter detection with suspend/resume
- **M7 — Release**: README polish, tag `v0.1.0`, GitHub Release with zip

Each milestone lands in a working state (app builds and runs at every step).
