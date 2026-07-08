# Builds SplitGuard into dist\SplitGuard-win-<arch>.zip. Requires only the .NET 8 SDK.
# -Installer additionally compiles dist\SplitGuard-Setup-<arch>-<version>.exe (needs Inno Setup 6).
param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64",
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$WgNtVersion = "0.10.1"

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
$dllArch = if ($Arch -eq "x64") { "amd64" } else { "arm64" }
$wgDll = Get-ChildItem -Recurse (Join-Path $wgExtract "*") -Filter "wireguard.dll" |
    Where-Object { $_.FullName -match [regex]::Escape($dllArch) } | Select-Object -First 1
if (-not $wgDll) { throw "wireguard.dll for $dllArch not found in the wireguard-nt archive." }

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
$out = Join-Path $root "dist\win-$Arch"
if (Test-Path $out) {
    try { Remove-Item -Recurse -Force $out -ErrorAction Stop }
    catch { throw "Cannot clean $out - close any running SplitGuard.exe first (it may be elevated)." }
}

Write-Host "Publishing ($Arch)..."
& $dotnetExe publish (Join-Path $root "src\SplitGuard") -c Release -r "win-$Arch" --self-contained `
    -p:PublishSingleFile=true -o $out -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item $wgDll.FullName (Join-Path $out "wireguard.dll")
Get-ChildItem $out -Filter "*.pdb" | Remove-Item

$zip = Join-Path $root "dist\SplitGuard-win-$Arch.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip

Write-Host "Done: $zip"

if ($Installer) {
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found - install it from https://jrsoftware.org/isinfo.php or omit -Installer." }
    $version = (Get-Content (Join-Path $root "VERSION")).Trim()
    Write-Host "Compiling installer..."
    & $iscc /Q "/DAppVersion=$version" "/DArch=$Arch" (Join-Path $root "installer\SplitGuard.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
    Write-Host "Done: $(Join-Path $root "dist\SplitGuard-Setup-$Arch-$version.exe")"
}
