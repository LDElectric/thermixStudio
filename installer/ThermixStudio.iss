; Inno Setup script for Thermix Studio
; Requires Inno Setup 6+

#define MyAppName "Thermix Studio"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "Thermix Studio"
#define MyAppExeName "ThermixStudio.App.exe"
#define MySourcePublishDir "..\artifacts\publish-app"

[Setup]
AppId={{D0D5A3A9-84A6-4A03-9D5E-5A8A9C649E11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=ThermixStudio-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "portuguesebrazilian"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "{#MySourcePublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Executar {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveAllData: Boolean;

function GetPublicDataDir(): string;
begin
  Result := ExpandConstant('{commondocs}\ThermixStudio');
end;

procedure InitializeWizard;
begin
  RemoveAllData := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    if MsgBox('Deseja remover também todos os dados do Thermix Studio (incluindo banco de dados e arquivos gerados)?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      RemoveAllData := True;
      DataDir := GetPublicDataDir();
      if DirExists(DataDir) then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
