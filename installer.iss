#ifndef PackagePath
  #error PackagePath is required
#endif
#ifndef OutputPath
  #error OutputPath is required
#endif
[Setup]
AppId={{8FBBD978-DF0D-438A-9C8B-06C5A40F7B02}
AppName=SimpleVPN 7
AppVersion=3.0.1
AppPublisher=SimpleVPN 7
DefaultDirName={code:DefaultFolder}
DefaultGroupName=SimpleVPN 7
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
MinVersion=6.1sp1
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\SimpleVPN.exe
OutputDir={#OutputPath}
OutputBaseFilename=SimpleVPN7-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=app.ico
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoDescription=SimpleVPN 7 — установка для Windows 7 x64
VersionInfoProductName=SimpleVPN 7
VersionInfoProductVersion=3.0.1

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PackagePath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\SimpleVPN"; Filename: "{app}\SimpleVPN.exe"; WorkingDir: "{app}"
Name: "{group}\SimpleVPN 7"; Filename: "{app}\SimpleVPN.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\SimpleVPN.exe"; Description: "Открыть SimpleVPN 7"; Flags: nowait postinstall skipifsilent
Filename: "{app}\SimpleVPN.exe"; Flags: nowait; Check: IsAutomaticUpdate

[Code]
function IsAutomaticUpdate(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:AUTOUPDATE|0}') <> '0');
end;

function DefaultFolder(Param: String): String;
begin
  if DirExists('D:\Programs\SimpleVPN') then
    Result := 'D:\Programs\SimpleVPN'
  else if DirExists('D:\Programs') then
    Result := 'D:\Programs\SimpleVPN'
  else
    Result := ExpandConstant('{localappdata}\SimpleVPN7');
end;

function InitializeSetup(): Boolean;
var
  Installed: Cardinal;
begin
  Result := (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5', 'Install', Installed) and (Installed = 1)) or
    (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Install', Installed) and (Installed = 1));
  if not Result then
    MsgBox('Включите встроенный компонент Windows «Microsoft .NET Framework 3.5.1» в панели управления. Скачивать .NET 4.8 для этой программы не нужно.', mbInformation, MB_OK);
end;
