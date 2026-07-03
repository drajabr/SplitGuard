# SplitGuard

Split DNS for WireGuard on Windows, in one small native app.

Connect one or more WireGuard tunnels and decide, per peer, which DNS server resolves which domains — for example `*.corp.example` through your office tunnel's DNS and `*.lab.internal` through your homelab's — while everything else keeps using your normal system DNS, untouched. Optionally pin one tunnel's DNS as your device-wide resolver, with automatic failover if that tunnel drops.

> **Status: release candidate.** Feature-complete; builds and packages via `build.cmd`. Tag `v0.1.0` to cut the first GitHub release. Verify tunnel + NRPT behavior on a real endpoint with admin rights. See [ROADMAP.md](ROADMAP.md).

## Why

WireGuard's `DNS =` setting is all-or-nothing: it takes over DNS for the whole machine. If you run more than one tunnel, or only want *some* names resolved through a tunnel, Windows has no friendly way to express that. This tool gives you exactly that, using the DNS policy mechanism built into Windows (NRPT) — no global DNS changes, no resident service, nothing to break.

## Features

- Full embedded WireGuard client (official WireGuardNT driver) — no other software needed
- Per-peer DNS and domain lists; add or remove domains live while connected
- Pin any peer's DNS as the device-wide resolver, with smart fallback and auto-failover
- Works alongside tunnels managed by the official WireGuard app (attach domains to them too)
- Everything not listed resolves via your normal system DNS — guaranteed untouched

## Install

Download the latest zip from the Releases page, extract, and run `SplitGuard.exe`. It requires administrator rights (UAC prompt) because DNS policy and the network driver are system-level. No installer, no background service — delete the folder to uninstall (use "unpin + disconnect" first to clear any active rules).

## Usage

1. **Add a tunnel** — drop a `.conf` file onto the window, or copy a config and press Ctrl+V.
2. Toggle the tunnel on — connection state, handshake, and traffic show on the card.
3. Edit the tunnel (pencil) to add domains to a peer (e.g. `*.corp.example`) — on save they resolve through that peer's DNS. Optionally pin one DNS as the device-wide resolver.

Verify resolution with PowerShell's `Resolve-DnsName <host>` (not `nslookup` — see caveats).

## How it works

The tool writes rules into the Windows Name Resolution Policy Table (NRPT) — the same OS facility enterprise VPNs use. Each rule says "queries for these domains go to this server"; queries matching no rule flow to your system DNS exactly as before. Rules are tagged, reconciled at startup, and removed when tunnels disconnect. If the app dies, worst case is stock DNS behavior — the design is fail-open.

## Build from source

Requires the .NET 8 SDK only:

```
.\build.cmd
```

(`build.cmd` is a thin wrapper that runs `build.ps1` with `-ExecutionPolicy Bypass`, so it works even when PowerShell script execution is disabled — the Windows default.)

Output: `dist/SplitGuard-win-x64.zip`. The official signed `wireguard.dll` is fetched automatically at build time.

## Caveats

- **Don't test with `nslookup`** — it bypasses Windows DNS policy by design. Use `Resolve-DnsName` in PowerShell.
- Browsers with "secure DNS" (DoH) enabled bypass any local DNS mechanism, including this one.
- On domain-joined machines, Group Policy DNS policy overrides local rules — the app warns you if that's the case.
- A tunnel's `DNS =` config line is used as that peer's suggested DNS server; it is deliberately never applied as global DNS.
- The DNS server you route to must be reachable inside the tunnel (within the peer's allowed IPs) — the app warns if it isn't.
