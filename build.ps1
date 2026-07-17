# Builds the SplitGuard installer: dist\SplitGuard-Setup-<version>.exe (x64 only).
# Requires the .NET 8 SDK and Inno Setup 6. The repo-root VERSION file is the single
# source of truth for the version (csproj, installer, and CI release tag all read it).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$WgNtVersion = "0.10.1"
$version = (Get-Content (Join-Path $root "VERSION")).Trim()

# A dotnet on PATH may be a runtime-only host; require one that actually has an SDK >= 8.
function Test-Sdk([string]$exe) {
    try { return [bool]((& $exe --list-sdks 2>$null) | Where-Object { [int]($_ -split "\.")[0] -ge 8 }) }
    catch { return $false }
}
$candidates = @()
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }
$candidates += (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
$dotnetExe = $candidates | Where-Object { (Test-Path $_) -and (Test-Sdk $_) } | Select-Object -First 1
if (-not $dotnetExe) { throw ".NET 8 SDK not found. Install it from https://dot.net" }

# Inno Setup compiler. Optional: without it the app still builds into dist\win-x64;
# only the installer packaging step is skipped.
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    Write-Warning "Inno Setup 6 (ISCC.exe) not found - building the app without an installer."
    Write-Warning "For the setup exe, install Inno Setup 6 from https://jrsoftware.org/isinfo.php"
}

# Fetch the official signed WireGuardNT driver DLL (never committed to the repo).
$cache = Join-Path $root ".cache"
$wgZip = Join-Path $cache "wireguard-nt-$WgNtVersion.zip"
if (-not (Test-Path $wgZip)) {
    New-Item -ItemType Directory -Force $cache | Out-Null
    Write-Host "Downloading wireguard-nt $WgNtVersion..."
    Invoke-WebRequest -UseBasicParsing "https://download.wireguard.com/wireguard-nt/wireguard-nt-$WgNtVersion.zip" -OutFile $wgZip
}
$wgExtract = Join-Path $cache "wireguard-nt"
if (Test-Path $wgExtract) { Remove-Item -Recurse -Force $wgExtract }
Expand-Archive $wgZip -DestinationPath $wgExtract
$wgDll = Get-ChildItem -Recurse (Join-Path $wgExtract "*") -Filter "wireguard.dll" |
    Where-Object { $_.FullName -match "amd64" } | Select-Object -First 1
if (-not $wgDll) { throw "wireguard.dll (amd64) not found in the wireguard-nt archive." }

# The app runs elevated and hides to tray, so stop it hard; escalate via UAC if needed.
if (Get-Process SplitGuard -ErrorAction SilentlyContinue) {
    Write-Host "Stopping running SplitGuard..."
    Get-Process SplitGuard -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    if (Get-Process SplitGuard -ErrorAction SilentlyContinue) {
        Write-Host "Instance is elevated - requesting admin rights to stop it (UAC prompt)..."
        Start-Process taskkill -ArgumentList "/IM", "SplitGuard.exe", "/F" -Verb RunAs -Wait
        Start-Sleep -Milliseconds 500
    }
}

# Clean output so stale artifacts never leak into a release.
$dist = Join-Path $root "dist"
if (Test-Path $dist) {
    try { Remove-Item -Recurse -Force $dist -ErrorAction Stop }
    catch { throw "Cannot clean $dist - close any running SplitGuard.exe first (it may be elevated)." }
}
$out = Join-Path $dist "win-x64"

Write-Host "Publishing (x64)..."
& $dotnetExe publish (Join-Path $root "src\SplitGuard.Desktop") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o $out -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item $wgDll.FullName (Join-Path $out "wireguard.dll")
Get-ChildItem $out -Filter "*.pdb" | Remove-Item

if ($iscc) {
    Write-Host "Compiling installer..."
    & $iscc /Q "/DAppVersion=$version" (Join-Path $root "installer\SplitGuard.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
    Write-Host "Done: $(Join-Path $dist "SplitGuard-Setup-$version.exe")"
}
else {
    Write-Host "Done (no installer): $out\SplitGuard.exe"
}
