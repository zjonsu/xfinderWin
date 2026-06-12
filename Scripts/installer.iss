; XFinder for Windows 설치 마법사 (Inno Setup 6)
; 빌드: dotnet publish 후  ISCC.exe Scripts\installer.iss

#define MyAppName "XFinder"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "zjonsu"
#define MyAppURL "https://github.com/zjonsu/xfinderWin"
#define MyAppExeName "XFinder.exe"
#define PublishDir "..\build\publish"

[Setup]
AppId={{8E2D6C3A-XFND-4W1N-9A11-202606120001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; 사용자 단위 설치 (UAC 불필요) — %LOCALAPPDATA%\Programs\XFinder
PrivilegesRequired=lowest
DefaultDirName={userpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\build
OutputBaseFilename=XFinder-Setup-{#MyAppVersion}
SetupIconFile=..\Resources\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 설정은 남겨둠 (%APPDATA%\XFinder) — 앱 폴더 잔재만 정리
Type: filesandordirs; Name: "{app}"
