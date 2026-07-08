; SplitGuard installer (Inno Setup 6). Compiled by build.ps1 -Installer, which passes
; AppVersion and Arch; sensible defaults below allow compiling the script directly too.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
; Strict per-arch gating: no x64-on-ARM emulation — the WireGuardNT driver is native-only.
#if Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  #define ArchAllowed "x64os"
#endif

[Setup]
AppId={{B7F5F6D1-4A3C-4E8B-9D2E-5C1A93A61E27}
AppName=SplitGuard
AppVersion={#AppVersion}
AppPublisher=SABA Energy
AppPublisherURL=https://github.com/saba-energy
DefaultDirName={autopf}\SplitGuard
DefaultGroupName=SplitGuard
DisableProgramGroupPage=yes
; Everything the app does (WireGuardNT driver, NRPT, scheduled tasks) is machine-level.
PrivilegesRequired=admin
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
OutputDir=..\dist
OutputBaseFilename=SplitGuard-Setup-{#Arch}-{#AppVersion}
SetupIconFile=..\src\SplitGuard\Assets\app.ico
UninstallDisplayIcon={app}\SplitGuard.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\win-{#Arch}\SplitGuard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\win-{#Arch}\wireguard.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\SplitGuard"; Filename: "{app}\SplitGuard.exe"
Name: "{autodesktop}\SplitGuard"; Filename: "{app}\SplitGuard.exe"; Tasks: desktopicon

[Run]
; First elevated run registers the no-UAC launcher task and the logon task.
Filename: "{app}\SplitGuard.exe"; Description: "{cm:LaunchProgram,SplitGuard}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop a running (elevated, tray-resident) instance, then let the app clear its own
; traces: NRPT rules, catch-all, and both scheduled tasks. Config in %ProgramData% stays.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM SplitGuard.exe /F"; Flags: runhidden; RunOnceId: "KillSplitGuard"
Filename: "{app}\SplitGuard.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupSplitGuard"

[Code]
// A live (possibly elevated tray) instance locks the exe: stop it before copying files.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SplitGuard.exe /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
