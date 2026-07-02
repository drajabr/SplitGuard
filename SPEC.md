# SPEC.md — implementation blueprint

Complete technical spec. Every decision is final — implement, don't research alternatives. Read AGENTS.md (rules) first. Owner-approved UI: see "UI" below.

## Project

`src/WgSplitDns/WgSplitDns.csproj`: `net8.0-windows`, `WinExe`, nullable, unsafe allowed, `ApplicationManifest=app.manifest`. Publish: `-r win-x64 --self-contained -p:PublishSingleFile=true`.
Packages: `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent` 11.2.*, `Microsoft.Management.Infrastructure` 3.0.*, `System.Security.Cryptography.ProtectedData` 8.0.*.
`app.manifest`: `requireAdministrator` + Win10/11 supportedOS GUIDs.

## Data model (`Models/`)

- `AppConfig.cs`: `AppConfig { int Version=1; PinnedDnsRef? PinnedDns; List<TunnelConfig> Tunnels; }` · `TunnelConfig { string Name; string PrivateKeyProtected; List<string> Addresses; List<PeerConfig> Peers; }` · `PeerConfig { string PublicKey; string? PresharedKeyProtected; string Endpoint; List<string> AllowedIps; string? Dns; List<string> Domains; }` · `PinnedDnsRef { string TunnelName; string PeerPublicKey; }`. System.Text.Json, camelCase.
- `WireGuardConf.cs`: parse/serialize wg-quick `.conf`. `[Interface]`: PrivateKey, Address (comma list), DNS, ListenPort ignored-but-preserved? No — ignore ListenPort/MTU/Table/Pre/PostUp-Down with a returned warnings list. `[Peer]`: PublicKey, PresharedKey, Endpoint, AllowedIPs, PersistentKeepalive. Imported `DNS=` attaches to the peer whose AllowedIps contain it, else first peer — never applied globally.

## Crypto (`Services/Curve25519.cs`)

Minimal X25519: clamp + scalar-mult-base for deriving public from private; keygen = `RandomNumberGenerator.GetBytes(32)` + clamp. Keys are 44-char base64 (32 bytes). Secrets at rest: `ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)` → base64 in config.json. Store: `%ProgramData%\WgSplitDns\config.json`, atomic write (tmp + `File.Move(overwrite)`) in `Services/RuleStore.cs`.

## WireGuard engine

### `Services/WireGuardNt.cs` — P/Invoke over `wireguard.dll` (loaded from exe dir)

Exports: `WireGuardCreateAdapter(name, tunnelType, guidPtr)` — note 0.10.x order is (Name, TunnelType), NOT (Pool, Name); getting this wrong yields win32 87 downstream — `WireGuardCloseAdapter`, `WireGuardSetConfiguration(handle, ptr, bytes)`, `WireGuardGetConfiguration(handle, ptr, ref bytes)` (stats), `WireGuardSetAdapterState(handle, state)` (`Down=0, Up=1`), `WireGuardGetAdapterLUID(handle, out ulong)`. Pool name `"WgSplitDns"` (distinguishes our adapters from the official client's `"WireGuard"` pool).
Config blob = contiguous: `WIREGUARD_INTERFACE`, then per peer `WIREGUARD_PEER` + its `WIREGUARD_ALLOWED_IP[]`. Copy struct layouts and flag enums verbatim from wireguard-nt `wireguard.h` (`WIREGUARD_INTERFACE{Flags,ListenPort,PrivateKey[32],PublicKey[32],PeersCount}`; `WIREGUARD_PEER{Flags,Reserved,PublicKey[32],PresharedKey[32],PersistentKeepalive,Endpoint SOCKADDR_INET,TxBytes,RxBytes,LastHandshake,AllowedIPsCount}`; `WIREGUARD_ALLOWED_IP{Address[16],AddressFamily,Cidr}`; flags: HAS_PUBLIC_KEY=1, HAS_PRESHARED_KEY=2, HAS_PERSISTENT_KEEPALIVE=4, HAS_ENDPOINT=8, REPLACE_ALLOWED_IPS=32; interface: HAS_PUBLIC_KEY=1, HAS_PRIVATE_KEY=2, REPLACE_PEERS=8).

### `Services/Netio.cs` — P/Invoke `iphlpapi.dll`

Addresses: `InitializeUnicastIpAddressEntry` + `CreateUnicastIpAddressEntry` on the adapter LUID. Routes: `InitializeIpForwardEntry` + `CreateIpForwardEntry2`, one per allowed-IP CIDR, metric 0/automatic — mask host bits off the destination first (`CreateIpForwardEntry2` returns 87 on non-canonical prefixes). Full-tunnel (`0.0.0.0/0` or `::/0`): replace with `0.0.0.0/1`+`128.0.0.0/1` (`::/1`+`8000::/1`) plus a host route to the resolved endpoint IP via the current best route (`GetBestRoute2`). No teardown bookkeeping: destroying the adapter removes its addresses and routes.

### `Services/TunnelManager.cs`

`Connect(TunnelConfig)`: resolve endpoint hostnames (`Dns.GetHostAddresses`) → create adapter → set configuration → assign addresses → add routes → state Up → notify NrptService. `Disconnect`: notify NrptService first (rules withdrawn), then close adapter. Stats: 1 s `DispatcherTimer`, `WireGuardGetConfiguration` → per-peer rx/tx (rates from deltas) + LastHandshake. Events: `TunnelStateChanged`, `StatsUpdated`.

## NRPT engine (`Services/NrptService.cs`)

Backend abstraction `INrptBackend` with two implementations, tried in order:
1. **CIM**: `CimSession.Create(null)`, namespace `root/StandardCimv2`, class `MSFT_DNSClientNrptRule`. Add = static method `Add` (params `Namespace string[]`, `NameServers string[]`, `Comment`, `DisplayName`); enumerate instances filtered by `Comment=="WG-SPLIT-DNS"`; remove = `DeleteInstance`.
2. **PowerShell fallback** (if CIM method missing/fails): `powershell.exe -NoProfile -NonInteractive -Command Add-DnsClientNrptRule/Get-/Remove-DnsClientNrptRule` with the same tag.

API: `ApplyPeerRules(peer)` (one rule per domain; `*.x` → namespace `.x`, bare `x` → `x`), `RemovePeerRules(peer)`, `SetCatchAll(string[] orderedServers)` (namespace `"."`), `RemoveCatchAll()`, `GetTaggedRules()`, `RemoveAllTagged()`, `Reconcile(config)` (startup: drop tagged rules with no matching enabled peer, re-add missing), `IsGpoNrptActive()` (any subkey under `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig`), `Flush()` = `[DllImport("dnsapi")] DnsFlushResolverCache()` after every mutation.
Catch-all chain: pinned server → other *connected* peers' DNS → `Services/SystemDns.cs` snapshot (DNS servers of physical up adapters, excluding WireGuard/loopback/virtual; refreshed via `NetworkChange.NetworkAddressChanged`). Rewrite chain on: pin change, any tunnel connect/disconnect, network change. Chain empty ⇒ remove catch-all (fail-open invariant).

## External tunnels (`Services/TunnelService.cs`)

`NetworkInterface.GetAllNetworkInterfaces()` where Description contains `"WireGuard Tunnel"` and name not in our pool → read-only card (no toggle/pencil-connection edits; DNS+domains+pin editable). Up/down via `NetworkChange` events → suspend/resume that card's NRPT rules.

## Test bar (`Services/TestService.cs`)

- Auto: `[DllImport("dnsapi")] DnsQuery_W(name, DNS_TYPE_A, DNS_QUERY_STANDARD, ...)` — goes through the OS resolver, honors NRPT.
- Direct server / System DNS entries: minimal UDP DNS client (hand-rolled A query, id randomized, 2 s timeout, one retry). Result: `ips · answered by <server> (<peerLabel>) in <ms>` or the error, single line.

## UI (Avalonia, Fluent theme)

`Views/Styles.axaml` tokens: input height 28, corner radius 4, `.item-card` (surface background, 4px radius, mono 12px, padding 4,9), `.add-card` (dashed 1px border — `Rectangle StrokeDashArray` overlay or `DashStyle` pen), `.label` (muted 12px). No per-view ad-hoc styles. Icons: `PathIcon` with inline Fluent path data (pin, pencil, copy, eye, x, plus, arrows, flask).

Window = header / scrollable card stack / test bar. Chrome: `ExtendClientAreaToDecorationsHint` so the header IS the title bar (drag via `BeginMoveDrag`; right padding clears caption buttons); theme cycle button (auto→light→dark) in the header; app icon `Assets/app.ico` (window + exe + tray); `TrayIcon` with Show/Exit menu — window close hides to tray, tunnels stay up; tray Exit runs full shutdown cleanup. Card body = `Grid 3*/7*`: left column interface facts (labels above values), right column peer blocks; external cards span both columns. All chips/inputs 26px tall, content vertically centered.
- **Header**: app icon+name left; `Add tunnel` button right (flyout: `Import .conf file…` via `StorageProvider`, `Paste config` via dialog with TextBox).
- **Tunnel card** (`Views/TunnelCard.axaml`): header row = status dot (green Up/gray Down) · name · spacer · [single-peer: `handshake Xs ago`] · `↑ rate` `↓ rate` (aggregate; hidden when disconnected) · `ToggleSwitch` · pencil. Interface lines (label col 90px): `Public key` = item-card (truncated middle) + `copy` link (full key to clipboard, 1.5 s checkmark swap); `Address` = item-cards. Then one **peer block** per peer (`Views/PeerBlock.axaml`, inner border): header = `Peer` + truncated pubkey + lock glyph if PSK + [multi-peer: handshake]; lines = `Endpoint` item-card · `Allowed IPs` item-cards · `DNS` item-card + pin ToggleButton + caption (`device DNS` / `device DNS · suspended`) · `Domains` item-cards with × + dashed `+ add`.
- **View mode live controls** (no save step): domain add (dashed card ⇄ inline TextBox; Enter commits+applies NRPT, Esc cancels; × removes+applies) and pin (exactly one device-wide across everything; pin elsewhere moves it; re-click unpins ⇒ system default; disabled if peer DNS empty).
- **Edit mode** (pencil; one card at a time; accent border + `editing` label; ToggleSwitch hidden): name/endpoint/DNS/public-key → TextBox; private key & PSK → masked TextBox + eye reveal + `generate` (private key: new keypair, public key label updates live); addresses/allowed-IPs get ×/add like domains (staged, not applied); `+ Add peer` / `Remove peer`; footer `Delete tunnel` (danger, left, confirm dialog) · `Cancel` · `Save`.
- **Save**: validate all → apply. Validation: CIDR (addr/allowed), `host:port` endpoint, IP for DNS, base64-32B keys, RFC-ish domain (`*.` prefix allowed), duplicate domain across peers rejected. Invalid = red border + tooltip, Save disabled. Warning (not block): peer DNS outside its allowed-IPs. DNS/domain changes ⇒ NRPT only; connection changes on a connected tunnel ⇒ quick reapply (SetConfiguration).
- **Startup**: load config → reconcile NRPT → detect external adapters → if `IsGpoNrptActive()` show warning banner ("Group Policy DNS rules override local ones — this tool's rules won't take effect on this machine").
- **Test bar**: flask icon · hostname TextBox (flex) · ComboBox ≤⅓ width (`Auto (effective)` default, one per peer-with-DNS `name — ip` / `name (peerN) — ip`, `System DNS`) · `Test`. Result: transient line below, only after a run.

ViewModels: `MainViewModel` (tunnel collection, pin arbitration, GPO banner, test), `TunnelViewModel` (view/edit state, staging buffer, validation), `PeerViewModel`. Plain `INotifyPropertyChanged`.

## build.ps1

1. Assert `dotnet` ≥ 8 else exit with message. 2. Download `https://download.wireguard.com/wireguard-nt/wireguard-nt-0.10.1.zip` to temp (skip if cached in `.cache/`), extract `bin/amd64/wireguard.dll`. 3. `dotnet publish src/WgSplitDns -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64`. 4. Copy `wireguard.dll` beside exe. 5. `Compress-Archive` → `dist/WgSplitDns-win-x64.zip`. Params: `-Arch x64|arm64` (arm64 uses `bin/arm64/`).

## .github/workflows/build-release.yml

`on: push (main, tags v*), pull_request`. Job `build` on `windows-latest`: checkout, `actions/setup-dotnet` v4 (8.0.x), `./build.ps1`, upload artifact `dist/*.zip`. Job `release` (needs build, `if: startsWith(github.ref,'refs/tags/v')`): download artifact, `softprops/action-gh-release@v2` with the zips.

## Reference gotchas

- `example.com` ≠ `.example.com` in NRPT (exact vs subdomains); UI `*.x` = `.x`.
- `nslookup` bypasses NRPT — verify with the in-app tester or `Resolve-DnsName`; inspect with `Get-DnsClientNrptRule`.
- NRPT longest-suffix match makes per-domain rules out-rank the `.` catch-all — relied upon, never reimplement.
- Adapter destruction removes its routes/addresses — disconnect needs no route bookkeeping.
- Never commit `wireguard.dll`; build fetches it.
