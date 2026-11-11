; Inno Setup 6 script for Dispatch. Build with installer\build.ps1 (publishes first, then compiles this).
; Per-user install, no admin rights. Dispatch's data (%LOCALAPPDATA%\Dispatch: settings, cache, BACKUPS) is kept on
; uninstall unless the user explicitly chooses to delete it.

#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{8F3C2A51-6B7E-4D2A-9C1E-2D5B7A1F0E44}
AppName=Dispatch
AppVersion={#AppVersion}
AppVerName=Dispatch {#AppVersion}
AppPublisher=Dispatch contributors
AppComments=A mod manager for Euro Truck Simulator 2
DefaultDirName={localappdata}\Programs\Dispatch
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=Dispatch-{#AppVersion}-setup
SetupIconFile=..\src\Dispatch.App\Assets\dispatch.ico
UninstallDisplayIcon={app}\Dispatch.exe
UninstallDisplayName=Dispatch
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Dispatch"; Filename: "{app}\Dispatch.exe"; Comment: "A mod manager for Euro Truck Simulator 2"
Name: "{autodesktop}\Dispatch"; Filename: "{app}\Dispatch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Dispatch.exe"; Description: "{cm:LaunchProgram,Dispatch}"; Flags: nowait postinstall skipifsilent

[Code]
// On uninstall, offer to delete Dispatch's own data folder. Default (and silent uninstall): keep it,
// because it holds the profile backups.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Dispatch');
    if DirExists(DataDir) then
      if SuppressibleMsgBox('Also delete Dispatch''s settings, cache and profile backups?' + #13#10 + DataDir + #13#10#13#10 +
                            'Choose No to keep your backups (recommended).',
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
