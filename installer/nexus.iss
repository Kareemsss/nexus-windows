; NEXUS installer (Inno Setup 6). Build it with scripts\build-installer.ps1, which publishes the app first and
; passes the version and the publish folder in. Per-user install: no administrator rights needed.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

#define AppName "NEXUS"
#define AppExe "Nexus.exe"
#define RepoUrl "https://github.com/Kareemsss/nexus-windows"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; Never change the AppId: Windows uses it to recognise upgrades and the uninstall entry.
AppId={{E97B11D2-5D4D-4FD8-AB09-BFC96E6ABCEF}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Kareemsss
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
; The same folder NEXUS used before it had an installer, so existing copies upgrade in place.
DefaultDirName={localappdata}\Programs\NEXUS
DisableDirPage=auto
; Installing into the existing folder is the expected upgrade path, not something to warn about.
DirExistsWarning=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=NEXUS-Setup-{#AppVersion}
SetupIconFile=..\src\Nexus.App\Assets\nexus.ico
WizardSmallImageFile=..\docs\nexus-icon.png
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; NEXUS is closed by the [Code] section below (and restarted afterwards), not by Restart Manager.
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start NEXUS when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Dynamic Island for Windows"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Dynamic Island for Windows"; Tasks: desktopicon

[Registry]
; The exact value NEXUS writes itself (Settings > General > Start with Windows), so both stay in sync.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "NEXUS"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start NEXUS now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only NEXUS's own icon lives here; make sure the folder goes even if a file handle lingered.
Type: filesandordirs; Name: "{app}\Assets"

[Code]
const
  InstanceMutex = 'Local\NEXUS.Instance';

var
  WasRunning: Boolean;
  StartupPreselected: Boolean;

function IsNexusRunning: Boolean;
begin
  Result := CheckForMutexes(InstanceMutex);
end;

{ Upgrading over a copy that already starts with Windows keeps that choice ticked. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not StartupPreselected then
  begin
    StartupPreselected := True;
    if RegValueExists(HKCU, '{#RunKey}', 'NEXUS') then
      WizardSelectTasks('startup');
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
  if IsNexusRunning then
    Result := Result + NewLine + NewLine + 'NEXUS is running. Setup will close it and start it again when it is done.';
end;

{ Close a running NEXUS so its files can be replaced or removed. Settings and the shelf are saved as they change. }
procedure StopNexus;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000); { the process ends asynchronously: let Windows release its file handles }
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := IsNexusRunning;
  if WasRunning then StopNexus;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if IsNexusRunning then StopNexus;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  { Unticking the startup option removes the entry. A silent upgrade leaves the user's choice alone. }
  if not WizardSilent and not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKCU, '{#RunKey}', 'NEXUS');

  { A silent upgrade (no "Start NEXUS now" checkbox) restarts NEXUS if it was running before. }
  if WizardSilent and WasRunning then
    Exec(ExpandConstant('{app}\{#AppExe}'), '--background', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value, DataDir: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  { Remove the startup entry, whether the installer or NEXUS's own setting created it. }
  if RegQueryStringValue(HKCU, '{#RunKey}', 'NEXUS', Value) and
     (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0) then
    RegDeleteValue(HKCU, '{#RunKey}', 'NEXUS');

  { Settings, the File Shelf and the log live outside the program folder. They are kept unless the user asks,
    never touched by a silent uninstall, and even then only moved to the Recycle Bin, so a mistake can be undone. }
  DataDir := ExpandConstant('{localappdata}\Nexus');
  Log('NEXUS data folder: silent=' + IntToStr(Ord(UninstallSilent)) + ' exists=' + IntToStr(Ord(DirExists(DataDir))));
  if UninstallSilent or not DirExists(DataDir) then Exit;
  if MsgBox('Also move your NEXUS settings, File Shelf and log to the Recycle Bin?' + #13#10 + #13#10 +
            'Choose No to keep them for the next time you install NEXUS.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;
  Log('NEXUS data folder: moving to the Recycle Bin');
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
       '-NoProfile -NonInteractive -Command "Add-Type -AssemblyName Microsoft.VisualBasic; ' +
       '[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(''' + DataDir + ''', ''OnlyErrorDialogs'', ''SendToRecycleBin'')"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
