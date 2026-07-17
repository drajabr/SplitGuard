# Patched libwg-go (dgram-tun fallback)

`src/SplitGuard.Android/Libs/wireguard-tunnel-*-splitguard*.aar` is the official
`com.wireguard.android:tunnel:1.0.20230706` AAR (Apache-2.0) with its four
`jni/*/libwg-go.so` binaries replaced by a rebuild whose `wgTurnOn` accepts a
**non-tun packet fd**: stock wireguard-go calls TUNGETIFF on the fd (SELinux denies
that ioctl on a socketpair from an untrusted app, and it would fail anyway), so the
patched `api-android.go` (this directory) falls back to a fixed-name/fixed-MTU
`dgramTun` device over the fd. That's what lets SplitGuard splice its split-DNS
relay (`TunPacketRelay`) between VpnService's real tun and wireguard-go.

Rebuild recipe (Windows; ~same on Linux):
1. `git clone --depth 1 --branch 1.0.20230706 https://git.zx2c4.com/wireguard-android`
2. Overwrite `tunnel/tools/libwg-go/api-android.go` with the copy in this directory.
3. Go 1.20+ and Android NDK r26; then per ABI
   (`arm64-v8a/arm64/aarch64-linux-android26-clang`, `armeabi-v7a/arm(GOARM=7)/armv7a-linux-androideabi26-clang`,
   `x86_64/amd64/x86_64-linux-android26-clang`, `x86/386/i686-linux-android26-clang`):
   `GOOS=android CGO_ENABLED=1 GOARCH=<goarch> CC=<ndk-clang> go build -tags linux \
    -ldflags="-X golang.zx2c4.com/wireguard/ipc.socketDirectory=/data/data/energy.saba.splitguard/cache/wireguard -buildid=" \
    -trimpath -buildvcs=false -o out/<abi>/libwg-go.so -buildmode c-shared`
4. Replace `jni/<abi>/libwg-go.so` inside the stock AAR (python zipfile; plain `zip -f` works too).

Known deltas vs the upstream AAR build: the Go-runtime `goruntime-boottime-over-monotonic`
patch is NOT applied (needs a patched Go toolchain); consequence is only that wg timers
use suspend-paused monotonic time, so a rekey can be delayed right after deep sleep.
Real tun fds still take the stock code path — the fallback only engages for relay fds.
