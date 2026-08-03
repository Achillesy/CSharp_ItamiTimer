; ItamiTimer's Windows installer (Inno Setup 6).
;
; Built by the repo root's pack-windows.ps1 -- don't run this by hand, it needs
; MyAppVersion and StageDir passed in via /D. This is the Windows counterpart of
; pack-macos.sh's .app/.dmg packaging.
;
; Unlike the macOS .dmg (which just tells the user in a Read Me to install the
; .NET Runtime themselves), this installer actively detects whether the .NET
; Desktop Runtime is present and offers to download + run the official installer
; if it's missing -- see the [Code] section below.
;
; The app itself installs per-user (PrivilegesRequired=lowest, no UAC prompt at
; all for the common case) -- {autopf} then resolves to the same
; %LOCALAPPDATA%\Programs location the project's own manual dev-publish command
; already uses. The .NET runtime installer, though, genuinely needs admin
; rights, and Inno's plain Exec() (CreateProcess) can't elevate a nested
; process on its own when the caller isn't already elevated. So that one step
; alone is launched via ShellExec's 'runas' verb, which pops its own UAC
; consent dialog just for that step -- everything else about installing
; ItamiTimer stays fully per-user.

#define MyAppName "ItamiTimer"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\installer-stage"
#endif
#define MyAppPublisher "Achilles.Newman"
#define MyAppURL "https://github.com/Achillesy/ItamiTimer"
#define MyAppExeName "ItamiTimer.exe"
; net10.0 -> Microsoft.WindowsDesktop.App's major version; keep this in sync if the TFM changes.
#define DotNetMajor "10"
#define DotNetRuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{DAE97B71-46FB-4310-B089-E64F091D04CE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ItamiTimer-{#MyAppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\src\ItamiTimer.App\tomato.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  RuntimeInstallerReady: Boolean;

// net10.0 needs Microsoft.WindowsDesktop.App 10.x: scan
// C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App for a subfolder
// starting with "10.". 64-bit install path only -- this project only ships win-x64.
// The runtime itself is always machine-wide regardless of whether ItamiTimer is
// installed per-user or per-machine, so this check doesn't change either way.
function IsDotNetDesktopRuntimeInstalled(const MajorVersion: string): Boolean;
var
  FindRec: TFindRec;
  BaseDir: string;
begin
  Result := False;
  BaseDir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BaseDir) then
    Exit;
  if FindFirst(BaseDir + '\' + MajorVersion + '.*', FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  RuntimeInstallerReady := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;

  if IsDotNetDesktopRuntimeInstalled('{#DotNetMajor}') then
    Exit;

  if SuppressibleMsgBox(
       'ItamiTimer needs the .NET {#DotNetMajor} Desktop Runtime, which was not found on this computer.' + #13#10 + #13#10 +
       'Setup can download it now (about 60 MB) from Microsoft and launch its installer right after ItamiTimer is installed. ' +
       'That step needs administrator approval (the runtime itself installs machine-wide); ItamiTimer itself does not.' + #13#10 + #13#10 +
       'Continue?',
       mbConfirmation, MB_YESNO, IDYES) <> IDYES then
    Exit;

  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetRuntimeUrl}', 'windowsdesktop-runtime-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      RuntimeInstallerReady := True;
    except
      if DownloadPage.AbortedByUser then
        Log('User aborted .NET runtime download')
      else
        // A failed download doesn't block this install: the framework-dependent apphost
        // shows its own "you need to install .NET" prompt on first launch if the runtime
        // is still missing, pointing at the official download page. That's the fallback.
        SuppressibleMsgBox(
          'Could not download the .NET Desktop Runtime automatically (' + GetExceptionMessage + ').' + #13#10 + #13#10 +
          'ItamiTimer will still be installed. If it fails to start, install the .NET ' + '{#DotNetMajor}' +
          ' Desktop Runtime (x64) manually from https://dotnet.microsoft.com/download and try again.',
          mbInformation, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep <> ssPostInstall) or not RuntimeInstallerReady then
    Exit;

  // ItamiTimer's own install stays per-user (no elevation). The runtime installer
  // genuinely needs admin, and a plain Exec() can't elevate a nested process when
  // the caller isn't already elevated -- ShellExec's 'runas' verb pops its own UAC
  // prompt just for this one step.
  if not ShellExec('runas', ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'),
       '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
    SuppressibleMsgBox(
      'Could not launch the .NET Desktop Runtime installer (administrator approval was likely declined).' + #13#10 + #13#10 +
      'ItamiTimer is installed. If it fails to start, install the .NET ' + '{#DotNetMajor}' +
      ' Desktop Runtime (x64) manually from https://dotnet.microsoft.com/download and try again.',
      mbInformation, MB_OK, IDOK);
end;
