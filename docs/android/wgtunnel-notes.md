# WG Tunnel (wgtunnel/android) — reference notes for the SplitGuard Android port

WG Tunnel is a mature FOSS Kotlin WireGuard/AmneziaWG client, **MIT-licensed**, so its
patterns (and code, with attribution) are freely portable to our C# heads.
Repo: https://github.com/wgtunnel/android · Docs: https://wgtunnel.com/docs ·
Architecture wiki: https://deepwiki.com/wgtunnel/android

## Positioning (decided 2026-07)

- WG Tunnel does NOT do per-domain split DNS (single `DNS=` per tunnel or a global
  default; domain-based routing is only feature request wgtunnel/android#1232), no
  multi-peer metric failover, and multiple simultaneous tunnels only in **kernel mode
  (root)**. SplitGuard Android's differentiator is exactly the split-DNS + unified
  desktop/mobile config; we do NOT compete on generic VPN-client features
  (auto-tunneling by SSID, per-app split tunneling, kill switch, TV, tiles — out of
  scope, possibly forever).
- Their kernel-mode-only multi-tunnel confirms: one VpnService tunnel at a time is the
  platform ceiling without root → failover stays display-only on Android.

## What to crib, phase by phase

### Phase 3 — SgVpnService / AndroidTunnelEngine (validate against their `core/service`)

They embed the official wireguard-android Go backend and manage the VpnService
themselves — same architecture we chose. Reference points in their tree
(`app/src/main/java/com/zaneschepke/wireguardautotunnel/`):

- `core/service/ServiceManager.kt` — service lifecycle coordinator (StateFlow of
  service instances; activation/state persistence). Mirrors what our
  `AndroidTunnelEngine` must own: consent → startForegroundService → state events.
- `core/service/` `TunnelForegroundService` — foreground VPN service; manifest uses
  foregroundServiceType **`systemExempted`** for Android 14+ (we planned
  `specialUse`; check both against current Play/AOSP policy — for a sideloaded APK
  `systemExempted` is the one they ship).
- Two notification channels (VPN + auto-tunnel); we need one VPN channel with the
  connect/disconnect action buttons.
- Their AndroidManifest.xml is the checklist for permissions/flags we'll need:
  `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SYSTEM_EXEMPTED`, `POST_NOTIFICATIONS`,
  `RECEIVE_BOOT_COMPLETED` (later), `android:persistent` quirks, VpnService intent
  filter + `BIND_VPN_SERVICE` permission on the service element.
- VpnService consent flow edge cases (revoked consent, always-on toggled by OS,
  another VPN app stealing the slot → `onRevoke()`): copy their handling; this is
  the classic source of "stuck connecting" bugs.
- Stats: they poll the backend for handshake/rx/tx and expose "real-time handshake
  monitoring" — cadence and the handshake-freshness heuristic match our desktop
  TunnelManager (poll ~1s, stale >3min). See wiki section
  `2.2.2-tunnel-state-and-statistics` and `7.5-tunnel-monitoring-and-health-checks`.
- Ping health: they run a ping-reachability check to decide tunnel-restart ("bounce
  tunnel on failed ping"). Reuse the idea for our health display; note they ping via
  InetAddress.isReachable / exec ping — if .NET `Ping` misbehaves on some OEMs, their
  fallback approach is the proven one.
- OEM battery-killer reality: they rely on the foreground service + user education,
  no REQUEST_IGNORE_BATTERY_OPTIMIZATIONS. Matches our plan.

### Phase 4 — split DNS

Nothing to take — they never touch the tun fd packet stream (which is why they can't
do per-domain DNS). Our TunPacketRelay + DnsForwarder design stands alone. Their
"dynamic DNS handling" is only endpoint re-resolution on server IP change — worth
copying LATER for endpoint hostname refresh on network change.

### Phase 5 — release/packaging

- They ship via GitHub releases + F-Droid + Play; APK signing config in
  `app/build.gradle.kts` is a working example of release signing values.
- Their versioning: versionCode monotonic ints — same scheme as our
  `major*10000+minor*100+patch`.

## Deliberately NOT taken (scope guards)

- Auto-tunneling engine (SSID/network-based activation) — large subsystem, out of scope.
- Per-app split tunneling (`addAllowedApplication`) — nice-to-have post-0.5.x; the
  Builder calls are trivial, the UI isn't.
- Kill switch / lockdown, AmneziaWG obfuscation, SOCKS5/HTTP proxy, quick tiles,
  Android TV — out of scope.
- Kernel-mode backend (root) — never.

## If SplitGuard-Android is ever abandoned

The fallback path is contributing per-domain split DNS to WG Tunnel upstream
(Kotlin, their tun handling would need the same fd-relay design) — tracked as an
option, not a plan.
