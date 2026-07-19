# Builds the SplitGuard installer: dist\SplitGuard-Setup-<version>.exe (x64 only).
# Requires the .NET 8 SDK and Inno Setup 6. The repo-root VERSION file is the single
# source of truth for the version (csproj, installer, and CI release tag all read it).
# Also builds the signed Android APK into dist\ when the android toolchain is present;
# pass -SkipAndroid for a Windows-only build.
param([switch]$SkipAndroid)
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

# --- Android APK -----------------------------------------------------------------------
# Publishes the signed Release APK into dist\SplitGuard-<version>.apk (same keystore and
# version scheme as CI). Best-effort: needs the `android` workload, JDK 17, and the Android
# SDK, so a Windows-only checkout skips it with a warning and still ships the installer.
# Pass -SkipAndroid to skip it deliberately.
if (-not $SkipAndroid) {
    $androidProj = Join-Path $root "src\SplitGuard.Android\SplitGuard.Android.csproj"
    $hasAndroid = $false
    try { $hasAndroid = [bool](((& $dotnetExe workload list) -join "`n") -match '(?im)^\s*android\b') } catch {}

    if (-not $hasAndroid) {
        Write-Warning "Android workload not installed - skipping APK. Install with: dotnet workload install android"
    }
    else {
        # Locate the Android SDK. MSBuild's own auto-detect is unreliable across shells
        # (env vars, elevation), so resolve it explicitly and validate the candidate is a
        # real SDK (has platform-tools / cmdline-tools / platforms). Checks env vars, the
        # per-user default, and adb/sdkmanager on PATH.
        $sdkCandidates = @(
            $env:ANDROID_HOME, $env:ANDROID_SDK_ROOT,
            "$env:LOCALAPPDATA\Android\Sdk",
            "$env:USERPROFILE\AppData\Local\Android\Sdk",
            "$env:ProgramFiles\Android\android-sdk",
            "${env:ProgramFiles(x86)}\Android\android-sdk"
        )
        foreach ($t in 'adb', 'sdkmanager') {
            $c = Get-Command $t -ErrorAction SilentlyContinue
            if ($c) { $sdkCandidates += (Split-Path (Split-Path $c.Source -Parent) -Parent) }
        }
        $sdk = $sdkCandidates | Where-Object {
            $_ -and (Test-Path $_) -and (
                (Test-Path (Join-Path $_ 'platform-tools')) -or
                (Test-Path (Join-Path $_ 'cmdline-tools')) -or
                (Test-Path (Join-Path $_ 'platforms'))) } | Select-Object -First 1

        if (-not $sdk) {
            Write-Warning "Android SDK not found - skipping APK (the Windows installer is still in $dist)."
            Write-Warning "  Set ANDROID_HOME to your SDK path (e.g. %LOCALAPPDATA%\Android\Sdk), or install it:"
            Write-Warning "  https://aka.ms/dotnet-android-install-sdk"
        }
        else {
            Write-Host "Building Android APK (SDK: $sdk)..."
            $androidProps = @("-p:AndroidSdkDirectory=$sdk")

            # JDK 17 (skip a stray JDK 21); pass it when found, else let MSBuild auto-detect.
            $jdk = @(
                $env:JAVA_HOME,
                "$env:ProgramFiles\Microsoft\jdk-17*",
                "$env:ProgramFiles\Eclipse Adoptium\jdk-17*",
                "$env:ProgramFiles\Java\jdk-17*",
                "$env:ProgramFiles\Android\Android Studio\jbr"
            ) | Where-Object { $_ } | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
                Where-Object { $_ -and (Test-Path (Join-Path $_.FullName "bin\java.exe")) } | Select-Object -First 1
            if ($jdk) { $androidProps += "-p:JavaSdkDirectory=$($jdk.FullName)"; Write-Host "  JDK: $($jdk.FullName)" }

            # OneDrive can hold a lock on obj\...\generated, failing the bindings clean; drop the
            # stale APK and generated sources so the signed Release publish starts clean.
            $abin = Join-Path $root "src\SplitGuard.Android\bin\Release"
            $agen = Join-Path $root "src\SplitGuard.Android\obj\Release\net8.0-android34.0\generated"
            Remove-Item -Recurse -Force $abin -ErrorAction SilentlyContinue
            Remove-Item -Recurse -Force $agen -ErrorAction SilentlyContinue

            $code = 1
            foreach ($attempt in 1..2) {   # one retry clears the OneDrive-locked generated dir
                & $dotnetExe publish $androidProj -c Release @androidProps -v q
                $code = $LASTEXITCODE
                if ($code -eq 0) { break }
                if ($attempt -eq 1) {
                    Write-Warning "Android publish failed - clearing intermediates and retrying once..."
                    Remove-Item -Recurse -Force $agen -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                }
            }

            if ($code -ne 0) {
                Write-Warning "Android APK build failed (see the dotnet output above) - the Windows installer is still in $dist."
            }
            else {
                $apk = Get-ChildItem $abin -Recurse -Filter "*-Signed.apk" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($apk) {
                    $apkOut = Join-Path $dist "SplitGuard-$version.apk"
                    Copy-Item $apk.FullName $apkOut -Force
                    Write-Host "Done: $apkOut"
                }
                else {
                    Write-Warning "Signed APK not found under $abin."
                }
            }
        }
    }
}

# The installer (and, if attempted, the APK) are done. The Android step is best-effort, so a
# failed/non-zero sub-build there must not make the whole script report failure — the critical
# steps above throw on error under $ErrorActionPreference. Return success explicitly.
exit 0
