# SPEC.md — implementation blueprint

Complete technical spec. Every decision is final — implement, don't research alternatives. Read AGENTS.md (rules) first. Owner-approved UI: see "UI" below.

## Project

`src/SplitGuard/SplitGuard.csproj`: `net8.0-windows`, `WinExe`, nullable, unsafe allowed, `ApplicationManifest=app.manifest`. Publish: `-r win-x64 --self-contained -p:PublishSingleFile=true`.
Packages: `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent` 11.2.*, `Microsoft.Management.Infrastructure` 3.0.*, `System.Security.Cryptography.ProtectedData` 8.0.*, `System.Threading.AccessControl` 8.0.*.
`app.manifest`: `asInvoker` + Win10/11 supportedOS GUIDs. The app still always runs elevated: `Program.Main` redirects a non-elevated start through the trigger-less `SplitGuardLaunch` scheduled task (`/RL HIGHEST`, registered on every elevated start while `UiPrefs.SkipUacLaunch` — so no UAC prompt after the first run), falling back to a `runas` relaunch. The show event carries an explicit ACL (Authenticated Users modify) so the non-elevated stub can surface an already-running instance. `--cleanup` (uninstaller hook) removes all tagged NRPT rules, the catch-all, and both scheduled tasks. The rejected alternative — a named-pipe privileged helper (old `skip-UAC` branch) — still prompted to start the helper and exposed an unauthenticated pipe.

## Data model (`Models/`)

- `AppConfig.cs`: `AppConfig { int Version=1; PinnedDnsRef? PinnedDns; List<TunnelConfig> Tunnels; }` · `TunnelConfig { string Name; string PrivateKeyProtected; List<string> Addresses; List<PeerConfig> Peers; }` · `PeerConfig { string PublicKey; string? PresharedKeyProtected; string Endpoint; List<string> AllowedIps; string? Dns; List<string> Domains; }` · `PinnedDnsRef { string TunnelName; string PeerPublicKey; }`. System.Text.Json, camelCase.
- `WireGuardConf.cs`: parse/serialize wg-quick `.conf`. `[Interface]`: PrivateKey, Address (comma list), DNS, ListenPort ignored-but-preserved? No — ignore ListenPort/MTU/Table/Pre/PostUp-Down with a returned warnings list. `[Peer]`: PublicKey, PresharedKey, Endpoint, AllowedIPs, PersistentKeepalive. Imported `DNS=` attaches to the peer whose AllowedIps contain it, else first peer — never applied globally.

## Crypto (`Services/Curve25519.cs`)

Minimal X25519: clamp + scalar-mult-base for deriving public from private; keygen = `RandomNumberGenerator.GetBytes(32)` + clamp. Keys are 44-char base64 (32 bytes). Secrets at rest: `ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)` → base64 in config.json. Store: `%ProgramData%\SplitGuard\config.json`, atomic write (tmp + `File.Move(overwrite)`) in `Services/RuleStore.cs`.

## WireGuard engine

### `Services/WireGuardNt.cs` — P/Invoke over `wireguard.dll` (loaded from exe dir)

Exports: `WireGuardCreateAdapter(name, tunnelType, guidPtr)` — note 0.10.x order is (Name, TunnelType), NOT (Pool, Name); getting this wrong yields win32 87 downstream — `WireGuardCloseAdapter`, `WireGuardSetConfiguration(handle, ptr, bytes)`, `WireGuardGetConfiguration(handle, ptr, ref bytes)` (stats), `WireGuardSetAdapterState(handle, state)` (`Down=0, Up=1`), `WireGuardGetAdapterLUID(handle, out ulong)`. Pool name `"SplitGuard"` (distinguishes our adapters from the official client's `"WireGuard"` pool).
Config blob = contiguous: `WIREGUARD_INTERFACE`, then per peer `WIREGUARD_PEER` + its `WIREGUARD_ALLOWED_IP[]`. Copy struct layouts and flag enums verbatim from wireguard-nt `wireguard.h` (`WIREGUARD_INTERFACE{Flags,ListenPort,PrivateKey[32],PublicKey[32],PeersCount}`; `WIREGUARD_PEER{Flags,Reserved,PublicKey[32],PresharedKey[32],PersistentKeepalive,Endpoint SOCKADDR_INET,TxBytes,RxBytes,LastHandshake,AllowedIPsCount}`; `WIREGUARD_ALLOWED_IP{Address[16],AddressFamily,Cidr}`; flags: HAS_PUBLIC_KEY=1, HAS_PRESHARED_KEY=2, HAS_PERSISTENT_KEEPALIVE=4, HAS_ENDPOINT=8, REPLACE_ALLOWED_IPS=32; interface: HAS_PUBLIC_KEY=1, HAS_PRIVATE_KEY=2, REPLACE_PEERS=8).

### `Services/Netio.cs` — P/Invoke `iphlpapi.dll`

Addresses: `InitializeUnicastIpAddressEntry` + `CreateUnicastIpAddressEntry` on the adapter LUID. Routes: `InitializeIpForwardEntry` + `CreateIpForwardEntry2`, one per allowed-IP CIDR, metric 0/automatic — mask host bits off the destination first (`CreateIpForwardEntry2` returns 87 on non-canonical prefixes). Full-tunnel (`0.0.0.0/0` or `::/0`): replace with `0.0.0.0/1`+`128.0.0.0/1` (`::/1`+`8000::/1`) plus a host route to the resolved endpoint IP via the current best route (`GetBestRoute2`). No teardown bookkeeping: destroying the adapter removes its addresses and routes.

### `Services/TunnelManager.cs`

`Connect(TunnelConfig)`: resolve endpoint hostnames (`Dns.GetHostAddresses`) → create adapter → set configuration → assign addresses → add routes → state Up → notify NrptService. `Disconnect`: notify NrptService first (rules withdrawn), then close adapter. Stats: 1 s `DispatcherTimer`, `WireGuardGetConfiguration` → per-peer rx/tx (rates from deltas) + LastHandshake. Events: `TunnelStateChanged`, `StatsUpdated`, `FailoverChanged`.

**Connected = handshake.** Adapter-up is only "connecting" (amber dot, no notification); the green dot + "Connected" notification fire on the first handshake, and a handshake older than 180 s drops the card back to amber ("Stalled").

**Keepalive ping (`PeerConfig.PingHost`).** Optional in-tunnel IP pinged once per keepalive period (25 s when keepalive is 0); per-ping timeout comes from the failover sensitivity, capped at the period. Keeps handshakes fresh and feeds health. Warn (not block) when outside the peer's allowed IPs. When the ping host is unique to its peer (across all connected tunnels) and inside that peer's allowed IPs, Connect pins a `/32` probe route to that adapter — mwan3-style — so the probe tests this path even while the peer is standby; a shared ping host can't be pinned and only counts while the peer's adapter would carry it.

**Failover for overlapping allowed IPs (`PeerConfig.Metric` / `FailoverMode` / `FailoverSensitivity`).** The same CIDR (exact match after masking; a `/0` groups as its two `/1` route halves) may be claimed by peers on one or many tunnels — a *route group*. Per group, members order by (Metric asc, tunnel name, peer index); the best *healthy* member is active. **Metric** is a 0–10 selector; values claimed by other members of the group are withdrawn from each peer's dropdown, and connect blocks on duplicates (`MetricConflict`) — a group always carries distinct metrics. **FailoverMode** per peer: `none` (always healthy: lowest metric carries traffic, health never demotes), `handshake` (handshake freshness), `ping` (handshake + ping-fail streak; requires a ping host — validation blocks connect without one). **FailoverSensitivity** `aggressive`/`normal`/`soft` maps to handshake staleness 135/180/300 s — anchored just past WireGuard's ~2 min rekey cadence, deliberately *not* N× keepalive, since keepalive doesn't refresh handshakes any faster — and ping thresholds 2 fails @ 1 s / 3 @ 3 s / 5 @ 5 s timeouts. 60 s first-handshake grace after connect. Recovery needs a healthy streak of one hold-down (starts at 30 s; doubles each time the path fails while active, capped at 10 min, reset after 10 min of stable health — a flapping link settles on the backup instead of oscillating). Arbitration, each poll tick: across adapters the active member's route keeps metric 0, every other adapter's route is pushed to ≥400 (`SetIpForwardEntry2`); within one adapter (WireGuard forbids true overlap) the grouped CIDR is assigned to that adapter's best member via live `SetConfiguration`. Dissolved groups restore metric 0. Balancing modes beyond metric failover (per-flow spreading, latency racing) were considered and rejected for now — Windows has no ECMP/policy-NAT without a WFP driver.

## NRPT engine (`Services/NrptService.cs`)

Backend abstraction `INrptBackend` with two implementations, tried in order:
1. **CIM**: `CimSession.Create(null)`, namespace `root/StandardCimv2`, class `MSFT_DNSClientNrptRule`. Add = static method `Add` (params `Namespace string[]`, `NameServers string[]`, `Comment`, `DisplayName`); enumerate instances filtered by `Comment=="WG-SPLIT-DNS"`; remove = `DeleteInstance`.
2. **PowerShell fallback** (if CIM method missing/fails): `powershell.exe -NoProfile -NonInteractive -Command Add-DnsClientNrptRule/Get-/Remove-DnsClientNrptRule` with the same tag.

API: `ApplyPeerRules(peer)` (one rule per domain; `*.x` → namespace `.x`, bare `x` → `x`), `RemovePeerRules(peer)`, `SetCatchAll(string[] orderedServers)` (namespace `"."`), `RemoveCatchAll()`, `GetTaggedRules()`, `RemoveAllTagged()`, `Reconcile(config)` (startup: drop tagged rules with no matching enabled peer, re-add missing), `IsGpoNrptActive()` (any subkey under `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig`), `Flush()` = `[DllImport("dnsapi")] DnsFlushResolverCache()` after every mutation.
Catch-all chain: pinned server → other *connected* peers' DNS → `Services/SystemDns.cs` snapshot (DNS servers of physical up adapters, excluding WireGuard/loopback/virtual; refreshed via `NetworkChange.NetworkAddressChanged`). Rewrite chain on: pin change, any tunnel connect/disconnect, network change. Chain empty ⇒ remove catch-all (fail-open invariant).

## External tunnels (`Services/TunnelService.cs`)

`NetworkInterface.GetAllNetworkInterfaces()` where Description contains `"WireGuard Tunnel"` and name not in our pool → read-only card (no toggle/pencil-connection edits; DNS+domains+pin editable). Up/down via `NetworkChange` events → suspend/resume that card's NRPT rules.

## Test bar — REMOVED (owner decision)

Resolution testing was cut from scope; users verify with `Resolve-DnsName`. A slim bottom status line (`StatusText`/`StatusOk`) shows import results and NRPT errors instead.

## UI (Avalonia, Fluent theme)

`Views/Styles.axaml` tokens: input height 28, corner radius 4, `.item-card` (surface background, 4px radius, mono 12px, padding 4,9), `.add-card` (dashed 1px border — `Rectangle StrokeDashArray` overlay or `DashStyle` pen), `.label` (muted 12px). No per-view ad-hoc styles. Icons: `PathIcon` with inline Fluent path data (pin, pencil, copy, eye, x, plus, arrows, flask).

Window = header / scrollable card stack / footer (status line + shortcut hints). Chrome: `ExtendClientAreaToDecorationsHint` + `TitleBarHeightHint=46`; header has no background so empty areas hit the OS caption and drag natively (logo/title `IsHitTestVisible=false`); app icon `Assets/app.ico` (window + exe + tray) recomposed at runtime in the accent color; `TrayIcon` lists tunnels (checkmark = connected, click toggles) + Show/Exit, green-tick variant while any tunnel is up — window close hides to tray, tunnels stay up; tray Exit runs full shutdown cleanup. Card body = `Grid 3*/7*`: left column interface facts, right column peer blocks; external cards span both columns.
- **Header controls** (right): one **Look** button cycling 6 curated theme+accent pairs (auto/light/rosé/ocean/ember/midnight) and one **Zoom** button (100/120/140% scaling the shared Fs*/metric resources). Fixed font: Segoe UI Variable. No add button — import via drag-drop or Ctrl+V; Ctrl+N makes an empty draft; Ctrl+E toggles raw-config editing; Ctrl+D twice deletes while editing.
- **Tunnel card** (`Views/TunnelCard.axaml`): header row = status dot (green Up/gray Down) · name · spacer · [single-peer: `handshake Xs ago`] · `↑ rate` `↓ rate` (aggregate; hidden when disconnected) · `ToggleSwitch` · pencil. Interface lines (label col 90px): `Public key` = item-card (truncated middle) + `copy` link (full key to clipboard, 1.5 s checkmark swap); `Address` = item-cards. Then one **peer block** per peer (`Views/PeerBlock.axaml`, inner border): header = `Peer` + truncated pubkey + lock glyph if PSK + [multi-peer: handshake]; lines = `Endpoint` item-card · `Allowed IPs` item-cards · `DNS` item-card + labeled pin button (outline pin `E718` + "pin as device DNS"; pinned = filled pin `E842` + "device DNS" in accent, warning color while suspended — the pinned *state* also shows in the bottom bar: filled pin + `Device DNS pinned · tunnel · server`, or `Pin suspended (tunnel off) · System DNS`) · `Keepalive` row (seconds · ping host) · `Metric` row (0–10 selector with group-taken values withdrawn · Failover mode selector · Sensitivity selector, disabled in None mode) · `Domains` item-cards with × + dashed `+ add`. Peer header shows live ping RTT and an `active`/`standby` badge while its allowed IPs are in a failover group.
- **View mode live controls** (no save step): domain add (dashed card ⇄ inline TextBox; Enter commits+applies NRPT, Esc cancels; × removes+applies) and pin (exactly one device-wide across everything; pin elsewhere moves it; re-click unpins ⇒ system default; disabled if peer DNS empty).
- **Edit mode** (pencil; one card at a time; accent border + `editing` label; ToggleSwitch hidden): name/endpoint/DNS/public-key → TextBox; private key & PSK → masked TextBox + eye reveal + `generate` (private key: new keypair, public key label updates live); addresses/allowed-IPs get ×/add like domains (staged, not applied); `+ Add peer` / `Remove peer`; footer `Delete tunnel` (danger, left, confirm dialog) · `Cancel` · `Save`.
- **Save**: validate all → apply. Validation: CIDR (addr/allowed), `host:port` endpoint, IP for DNS, base64-32B keys, RFC-ish domain (`*.` prefix allowed), duplicate domain across peers rejected. Invalid = red border + tooltip, Save disabled. Warning (not block): peer DNS outside its allowed-IPs. DNS/domain changes ⇒ NRPT only; connection changes on a connected tunnel ⇒ quick reapply (SetConfiguration).
- **Startup**: load config → reconcile NRPT → detect external adapters → if `IsGpoNrptActive()` show warning banner ("Group Policy DNS rules override local ones — this tool's rules won't take effect on this machine").
- **Test bar**: flask icon · hostname TextBox (flex) · ComboBox ≤⅓ width (`Auto (effective)` default, one per peer-with-DNS `name — ip` / `name (peerN) — ip`, `System DNS`) · `Test`. Result: transient line below, only after a run.

ViewModels: `MainViewModel` (tunnel collection, pin arbitration, GPO banner, test), `TunnelViewModel` (view/edit state, staging buffer, validation), `PeerViewModel`. Plain `INotifyPropertyChanged`.

## build.ps1

x64 only (arm64 dropped for now); the installer is the one and only build artifact. 1. Assert an SDK-bearing `dotnet` ≥ 8 (PATH may hold a runtime-only host) and locate ISCC.exe — fail fast if either is missing. 2. Download `https://download.wireguard.com/wireguard-nt/wireguard-nt-0.10.1.zip` (cached in `.cache/`), extract `bin/amd64/wireguard.dll`. 3. Stop a running instance, wipe `dist/` so stale artifacts never leak into a release. 4. `dotnet publish src/SplitGuard -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64`, copy `wireguard.dll` beside the exe, drop pdbs. 5. Compile the installer → `dist/SplitGuard-Setup-<version>.exe`. Version = repo-root `VERSION` file (also read by csproj and the CI release tag — single source of truth).

## installer/SplitGuard.iss (Inno Setup 6)

Compiled by `build.ps1` with `/DAppVersion` from `VERSION`; `ArchitecturesAllowed=x64os` (strict — the WireGuardNT driver is native-only, no ARM emulation). Installs `SplitGuard.exe` + `wireguard.dll` to `{autopf}\SplitGuard`, Start Menu shortcut, optional desktop icon, `PrivilegesRequired=admin`. Install kills a running instance first (`PrepareToInstall` → taskkill). Uninstall: taskkill → `SplitGuard.exe --cleanup` (NRPT + scheduled tasks) → remove files; `%ProgramData%\SplitGuard\config.json` is kept.

## .github/workflows/build-release.yml

`on: push (main, tags v*), pull_request`. Job `build` on `windows-latest`: checkout, `actions/setup-dotnet` v4 (8.0.x), ensure Inno Setup (choco fallback if the runner image ever drops it), `./build.ps1`, upload `dist/SplitGuard-Setup-*.exe`. Job `release` (needs build, push events): resolve the tag from `VERSION` (or the pushed `v*` tag); if that tag doesn't exist yet, `softprops/action-gh-release@v2` publishes it with the installer as the sole asset. Bump `VERSION` + push to main = release.

## Reference gotchas

- `example.com` ≠ `.example.com` in NRPT (exact vs subdomains); UI `*.x` = `.x`.
- `nslookup` bypasses NRPT — verify with the in-app tester or `Resolve-DnsName`; inspect with `Get-DnsClientNrptRule`.
- NRPT longest-suffix match makes per-domain rules out-rank the `.` catch-all — relied upon, never reimplement.
- Adapter destruction removes its routes/addresses — disconnect needs no route bookkeeping.
- Never commit `wireguard.dll`; build fetches it.
