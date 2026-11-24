; PhoneBox for Windows: a per-user installer (no administrator needed) built by tools/make-windows-installer.ps1.
;
; It installs into %LOCALAPPDATA%\PhoneBox\app, the folder the agent's own updater and scripts\install.ps1 use
; (AppPaths.InstallDir), so a copy installed here updates itself from the GitHub release like any other install. The folder
; is fixed on purpose (no folder page): the updater only replaces an agent that runs from it.
; Settings (%APPDATA%\PhoneBox) and the log are kept when PhoneBox is uninstalled, as scripts\uninstall.ps1 does without -Purge.
;
; Needs Inno Setup 6.3 or later (ArchitecturesAllowed=x64compatible). Defines passed with /D:
;   AppVersion     the release version, e.g. 0.9.28
;   SourceExe      the published PhoneBoxAgent.exe (signed, when the release signs it)
;   SourceNotices  THIRD-PARTY-NOTICES.txt
;   OutDir         where PhoneBox-<version>-win-x64-setup.exe is written

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef SourceExe
  #error Pass /DSourceExe=path\to\PhoneBoxAgent.exe
#endif
#ifndef SourceNotices
  #error Pass /DSourceNotices=path\to\THIRD-PARTY-NOTICES.txt
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
; never change: it is how a later installer recognises (and upgrades) this one
AppId={{F4854D0A-0000-4AB6-A333-D4E0280DCB74}
AppName=PhoneBox
AppVersion={#AppVersion}
AppVerName=PhoneBox {#AppVersion}
AppPublisher=PhoneBox
AppPublisherURL=https://github.com/ersinkoc/phonebox
AppSupportURL=https://github.com/ersinkoc/phonebox/issues
AppUpdatesURL=https://github.com/ersinkoc/phonebox/releases
DefaultDirName={localappdata}\PhoneBox\app
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=PhoneBox-{#AppVersion}-win-x64-setup
SetupIconFile=..\..\agent\PhoneBox.Agent\phonebox.ico
UninstallDisplayIcon={app}\PhoneBoxAgent.exe
UninstallDisplayName=PhoneBox
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; the agent is stopped by hand (see [Code]); the Restart Manager would ask the user to close it
; Setup restores the last install's tasks over /TASKS, so a task could never be unticked from the command line
UsePreviousTasks=no
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start PhoneBox with Windows"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceNotices}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; the same shortcuts scripts\install.ps1 and the tray menu make, so they recognise each other
Name: "{userprograms}\PhoneBox"; Filename: "{app}\PhoneBoxAgent.exe"; WorkingDir: "{app}"; Comment: "PhoneBox agent"
Name: "{userstartup}\PhoneBox"; Filename: "{app}\PhoneBoxAgent.exe"; WorkingDir: "{app}"; Comment: "PhoneBox agent"; Tasks: autostart

[Run]
Filename: "{app}\PhoneBoxAgent.exe"; WorkingDir: "{app}"; Description: "Start PhoneBox now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; the whole app folder is ours: what the updater leaves behind (*.old, downloads) goes too
Type: filesandordirs; Name: "{app}"
; also the Start-with-Windows shortcut the tray menu may have added after install
Type: files; Name: "{userstartup}\PhoneBox.lnk"

[Code]
procedure StopAgent;
var
  Code: Integer;
begin
  { a running agent holds PhoneBoxAgent.exe; 128 means it was not running, which is fine }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PhoneBoxAgent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // /DIR could point the app folder anywhere, and uninstalling deletes it: only the folder the agent updates itself in is accepted
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{localappdata}\PhoneBox\app')) <> 0 then
  begin
    Result := 'PhoneBox installs only into ' + ExpandConstant('{localappdata}\PhoneBox\app') + ' (the folder it updates itself in).';
    Exit;
  end;
  StopAgent;
  Result := '';
end;

var
  AutostartPreselected: Boolean;

procedure CurPageChanged(CurPageID: Integer);
begin
  { a Start-with-Windows shortcut that is already there (made by the installer or by the tray menu) keeps its box ticked, once }
  if (CurPageID = wpSelectTasks) and not AutostartPreselected then
  begin
    AutostartPreselected := True;
    if FileExists(ExpandConstant('{userstartup}\PhoneBox.lnk')) then WizardSelectTasks('autostart');
  end;
end;

function KeepAutostart: Boolean;
begin
  // A silent install with no /TASKS says nothing about it, so a shortcut that is already there stays. Otherwise the box in the
  // wizard (ticked from the start when the shortcut exists, see CurPageChanged) or /TASKS decides.
  if WizardSilent and (ExpandConstant('{param:TASKS|}') = '') then Result := True
  else Result := WizardIsTaskSelected('autostart');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // installing with "Start PhoneBox with Windows" unticked leaves no Startup shortcut, so unticking it on an upgrade turns it off
  if CurStep = ssInstall then
  begin
    Log('autostart kept: ' + IntToStr(Ord(KeepAutostart)) + ', task selected: ' + IntToStr(Ord(WizardIsTaskSelected('autostart'))));
    if not KeepAutostart then DeleteFile(ExpandConstant('{userstartup}\PhoneBox.lnk'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then StopAgent;
end;
