# AGENTS.md — WG Split DNS

Guide for AI agents and human contributors. Everything here was agreed with the project owner during design; do not silently deviate. [ROADMAP.md](ROADMAP.md) holds the full design record and milestone order — read both before touching code.

## What this project is

A native desktop utility (Avalonia UI, C#/.NET 8) with one purpose: split DNS for WireGuard on Windows. It embeds its own WireGuard client (WireGuardNT via the official `wireguard.dll`) and steers DNS with Windows NRPT rules: per peer, "this DNS server resolves these domains", optionally pinning one peer's DNS as the device-wide resolver with automatic failover.

**Non-goals** (reject scope creep toward these): DNS blocking, local DNS forwarder/proxy, general VPN manager features, per-app routing, MSIX packaging, tray daemon / Windows service, kill switch.

## Current status

Pre-implementation. Folder skeleton and docs only. Implementation starts at milestone M1 (see ROADMAP.md) **only after the owner confirms the plan**.

## Architecture map

One window, plain MVVM (no MVVM framework — `INotifyPropertyChanged` by hand). Services are the only code with side effects; each has a single responsibility and hard boundaries:

| Service | Owns | Must never |
|---|---|---|
| `TunnelManager` | Our tunnels: adapter lifecycle via `wireguard.dll`, config apply, interface IPs, routes, handshake/transfer polling | Touch NRPT, touch adapters it didn't create |
| `NrptService` | All NRPT reads/writes via CIM (`root/StandardCimv2`, `MSFT_DNSClientNrptRule`), catch-all rule + server chain, reconciliation, GPO-NRPT detection, resolver cache flush | Touch any rule not tagged `WG-SPLIT-DNS`; set adapter DNS; leave a catch-all whose entire chain is down |
| `RuleStore` | Load/save `%ProgramData%\WgSplitDns\config.json`, DPAPI protect/unprotect for private keys and PSKs | Persist key material unencrypted; log secrets |
| `TunnelService` | Detection of *external* WireGuard adapters (official client), up/down events, suspend/resume of their DNS rules | Manage/modify external tunnels |
| `TestService` | Test-bar resolution: "Auto" via OS resolver (`DnsQueryEx`, honors NRPT); direct queries to a specific server | Cache results; touch system state |

Only `NrptService` writes NRPT. Only `TunnelManager` talks to the driver. UI never calls Win32/CIM directly.

## Hard invariants — breaking any of these is a bug, full stop

1. **Fail-open DNS.** No reachable state may leave the machine without working DNS if this app crashes or is killed. Never set adapter DNS. Never bind port 53. The NRPT catch-all must always contain at least one live server or not exist.
2. **Only tagged rules.** Create, modify, and delete NRPT rules carrying the `WG-SPLIT-DNS` marker exclusively. GPO/VPN-client/manual rules are read-only, always.
3. **Unlisted domains are never affected.** Without a pin there is no catch-all; queries for domains not in any peer's list must behave as if the app didn't exist. "Remove everything tagged" must restore 100% stock behavior.
4. **Per-domain rules out-rank the catch-all** via NRPT longest-suffix matching. This is relied upon by design — do not "fix" or reimplement precedence.
5. **Pinned tunnel down ⇒ catch-all rewritten immediately** to the next live server in the chain (other connected peers' DNS → snapshotted system DNS) or removed. Also on network changes: refresh the snapshotted system servers in the chain.
6. **Secrets stay protected.** Private keys and PSKs: DPAPI (machine scope) at rest, masked in UI (eye-reveal only), never in logs, never in exceptions, only exported in an explicit user-initiated `.conf` export.
7. **Reconcile at startup** against live NRPT state (crash recovery — no invisible orphaned rules) and warn visibly if GPO NRPT is present (local rules would be ignored; silent no-op is unacceptable).
8. **Flush the resolver cache** (`dnsapi.dll!DnsFlushResolverCache`) after every NRPT change.
9. **Imported `DNS =` lines are suggestions only** — attached as the peer's DNS value, never applied as global/adapter DNS.
10. **No committed binaries.** `wireguard.dll` is fetched at build time by `build.ps1`/CI from download.wireguard.com. Official signed builds only.

## NRPT semantics you must know

- `example.com` and `.example.com` are **different namespaces** (exact vs subdomain-inclusive). UI convention: `*.example.com` normalizes to the subdomain-inclusive form.
- `nslookup` **bypasses NRPT** (own resolver). Never use it to verify behavior; use the in-app test bar or `Resolve-DnsName`. Inspect state with `Get-DnsClientNrptRule`.
- Browsers with DoH enabled bypass any local DNS mechanism. Documented caveat, not a bug.
- Catch-all namespace is `.`. A rule's server list is tried in order on timeout — semantics are best-effort, not contractual.

## WireGuard integration notes

- `wireguard.dll` (WireGuardNT) does the protocol; we do: adapter create/destroy, `SetConfiguration`, interface IP assignment, one route per allowed-IP entry, `0.0.0.0/1` + `128.0.0.0/1` split + endpoint host route for full-tunnel configs, per-peer handshake/transfer polling (~1s).
- Model mirrors WireGuard exactly: tunnel = interface + N peers; **one endpoint per peer** ("multiple endpoints" = multiple peers). Peer fields: public key, optional PSK, endpoint, allowed IPs — plus our layer: DNS, domains.
- DNS/domain edits never touch the tunnel. Connection-field edits on a connected tunnel = quick config reapply, surfaced in UI.
- External tunnels (official client) are detected read-only; DNS/domains may be attached; their rules suspend/resume with adapter state.

## UI rules (agreed through mockups — see ROADMAP.md "UI specification" for the full spec)

- Avalonia 11, Fluent theme, stock controls plus one small shared style set (dense 28px inputs, compact item cards, dashed add button) in `Views/`. No per-view ad-hoc styling, no pills, 4px corners, one window, sentence case. (Decision record: WinUI 3 was the original plan; switched to Avalonia for build simplicity and the dense card UI — see ROADMAP.md.)
- One field per line inside cards; every value is a small square-corner item card.
- **Always live in view mode**: domain cards (inline add via Enter, × to remove — instant NRPT ops) and the DNS pin. Everything else is gated behind the pencil (edit mode): one card at a time, accent border, connect toggle hidden, Save validates everything before anything applies, Delete tunnel behind a confirm dialog.
- **Pin**: exactly one device-wide, across all tunnels/peers; re-click to unpin (= system default); disabled when peer has no DNS; caption states "device DNS" / "device DNS · suspended".
- Stats: header shows `handshake · ↑ · ↓` (single peer) or aggregate ↑/↓ with per-peer handshakes in peer-block headers (multi-peer); nothing shown when disconnected.
- Test bar: input (flex) · server selector (≤ ⅓ width) · Test; result is a transient single line below, present only after a run.
- Validation: CIDR, `host:port`, IP, base64-32-byte keys, domain syntax; duplicates across peers rejected; DNS outside the peer's allowed IPs = warning, not a block.

## Build, run, verify (from M1 onward)

- Local build: `.\build.ps1` — requires .NET 8 SDK only; produces `dist/WgSplitDns-win-x64.zip`. Dev loop: `dotnet build src/WgSplitDns`.
- The app requires elevation (manifest); launch prompts UAC. Real NRPT/driver behavior can only be verified on a Windows machine as admin.
- Verify NRPT state: `Get-DnsClientNrptRule` (look for the `WG-SPLIT-DNS` comment). Verify resolution: in-app test bar or `Resolve-DnsName` — **never `nslookup`**.
- CI: GitHub Actions `windows-latest`; push/PR = build; tag `v*` = build + GitHub Release with zips.

## Conventions

- C# 12 / .NET 8, nullable enabled, `net8.0-windows`.
- No new NuGet dependencies without strong justification (current set: Avalonia 11 + Fluent theme, Microsoft.Management.Infrastructure).
- Plain MVVM; view models own state, services own side effects, views own nothing.
- Comments only for constraints code can't express (e.g., why a route trick exists). Match existing style.
- UI copy: sentence case, no exclamation marks, verb-first buttons.

## Definition of done (any change)

1. Builds clean via `.\build.ps1`.
2. All invariants above hold — especially: kill the app mid-operation, relaunch, reconciliation leaves no orphaned tagged rules; unpin + disconnect-all leaves zero tagged NRPT rules (`Get-DnsClientNrptRule` clean).
3. No secret material in logs or UI beyond masked fields.
4. UI changes stay within the shared-style rule and the interaction spec.
5. README/ROADMAP updated if behavior or scope moved.

## Release flow

1. Update version in csproj, finish milestone, verify definition of done.
2. Tag `vX.Y.Z`, push tag → Actions builds and attaches `WgSplitDns-win-x64.zip` (+arm64 if enabled) to a GitHub Release.
3. Release notes: user-facing changes only, one line each.
