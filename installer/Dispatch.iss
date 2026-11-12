; Inno Setup 6 script for Dispatch. Build with installer\build.ps1 (publishes first, then compiles this).
; Per-user install, no admin rights. Dispatch's data (%LOCALAPPDATA%\Dispatch: settings, cache, BACKUPS) is kept on
; uninstall unless the user explicitly chooses to delete it.

#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{8F3C2A51-6B7E-4D2A-9C1E-2D5B7A1F0E44}
AppName=Dispatch
AppVersion={#AppVersion}
AppVerName=Dispatch {#AppVersion}
AppPublisher=Dispatch contributors
AppComments=A mod manager for Euro Truck Simulator 2 and American Truck Simulator
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
; The installer asks for the language when Windows' language isn't English; Dispatch itself picks its language separately.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.DeleteDataQuestion=Also delete Dispatch's settings, cache and profile backups?
english.KeepBackupsHint=Choose No to keep your backups (recommended).
german.DeleteDataQuestion=Auch die Einstellungen, den Cache und die Profilsicherungen von Dispatch löschen?
german.KeepBackupsHint=Wähle Nein, um deine Sicherungen zu behalten (empfohlen).
spanish.DeleteDataQuestion=¿Eliminar también los ajustes, la caché y las copias de perfiles de Dispatch?
spanish.KeepBackupsHint=Elige No para conservar tus copias (recomendado).
french.DeleteDataQuestion=Supprimer aussi les paramètres, le cache et les sauvegardes de profils de Dispatch ?
french.KeepBackupsHint=Choisissez Non pour garder vos sauvegardes (recommandé).
polish.DeleteDataQuestion=Usunąć też ustawienia, pamięć podręczną i kopie profili Dispatch?
polish.KeepBackupsHint=Wybierz Nie, aby zachować kopie (zalecane).
brazilianportuguese.DeleteDataQuestion=Excluir também as configurações, o cache e os backups de perfis do Dispatch?
brazilianportuguese.KeepBackupsHint=Escolha Não para manter seus backups (recomendado).
russian.DeleteDataQuestion=Удалить также настройки, кэш и резервные копии профилей Dispatch?
russian.KeepBackupsHint=Выберите «Нет», чтобы сохранить копии (рекомендуется).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Dispatch"; Filename: "{app}\Dispatch.exe"; Comment: "A mod manager for Euro Truck Simulator 2 and American Truck Simulator"
Name: "{autodesktop}\Dispatch"; Filename: "{app}\Dispatch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Dispatch.exe"; Description: "{cm:LaunchProgram,Dispatch}"; Flags: nowait postinstall skipifsilent
; Dispatch's own updater runs "setup.exe /SILENT /RELAUNCH=1" and expects to be started again afterwards.
Filename: "{app}\Dispatch.exe"; Flags: nowait; Check: Relaunch

[Code]
function Relaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

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
      if SuppressibleMsgBox(CustomMessage('DeleteDataQuestion') + #13#10 + DataDir + #13#10#13#10 +
                            CustomMessage('KeepBackupsHint'),
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
