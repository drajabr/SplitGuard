# SplitGuard for Android

The Android build brings SplitGuard's defining feature — **per-domain split DNS** — to
the phone, using the same `.conf` import and tunnel/peer model as the desktop app. It is
**not** a general-purpose WireGuard client; for that, use the official app or
[WG Tunnel](https://github.com/wgtunnel/android).

## Install

Download `SplitGuard-<version>.apk` from the
[latest release](https://github.com/drajabr/SplitGuard/releases/latest) and sideload it
(enable "install unknown apps" for your browser/file manager). Updates: install the newer
APK over the top — the signing key is stable across releases, so it upgrades in place.

## What works

- Import a WireGuard `.conf`, connect/disconnect via Android's VpnService (bundled
  wireguard-go).
- **Per-domain DNS routing** (NRPT-equivalent): domains you attach to a peer resolve
  through that peer's DNS while connected; everything else uses your normal system DNS.
  An in-tunnel DNS forwarder does this — it is on by default and can be turned off under
  Settings → "Per-domain DNS routing" (off = standard per-tunnel DNS from the config).
- Live throughput/handshake stats and handshake-freshness health.
- The full desktop theme/accent/font/zoom system.

## Limitations (0.5.0)

- **One tunnel at a time.** Android's VpnService allows a single VPN tunnel without root,
  so connecting a second tunnel replaces the first. Multi-tunnel **failover** (a desktop
  feature) is therefore not available; peer health is shown but not arbitrated.
- **DNS interception is IPv4-only** and covers UDP plus TCP-fallback for truncated
  answers; there is no packet-level TCP/53 interception.
- No auto-tunneling, per-app split tunneling, kill switch, or quick-settings tile — out of
  scope by design.

## Building

`dotnet publish src/SplitGuard.Android/SplitGuard.Android.csproj -c Release` (JDK 17 +
the `android` workload + Android SDK 34). The APK is signed with the committed release
keystore (`build/android`, passwords documented in the csproj) so sideloaded installs
update in place. The bundled wireguard-go is a patched rebuild — see
[`tools/libwg-go-patch`](../../tools/libwg-go-patch/README.md).
