; SplitGuard installer (Inno Setup 6). Compiled by build.ps1, which passes AppVersion
; from the repo-root VERSION file; the default below allows compiling directly too.
; x64 only for now (strict: no x64-on-ARM emulation — the WireGuardNT driver is native-only).

#ifndef AppVersion
  #define AppVersion "0.0.0"
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
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir=..\dist
OutputBaseFilename=SplitGuard-Setup-{#AppVersion}
SetupIconFile=..\src\SplitGuard\Assets\app.ico
UninstallDisplayIcon={app}\SplitGuard.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\win-x64\SplitGuard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\win-x64\wireguard.dll"; DestDir: "{app}"; Flags: ignoreversion

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
