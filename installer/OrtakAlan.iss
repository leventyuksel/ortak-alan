#ifndef MyAppVersion
  #define MyAppVersion "1.2.4"
#endif

#define MyAppName "Ortak Alan"
#define MyAppPublisher "LYazılım"
#define MyAppExeName "OrtakAlan.exe"

[Setup]
AppId={{C3A91E47-6D2B-4F18-A5E0-8B7C4D2F91A6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
DefaultDirName={localappdata}\OrtakAlan\uygulama
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=OrtakAlan-Setup
SetupIconFile=..\Assets\ikon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek kısayollar:"; Flags: checkedonce

[Files]
Source: "..\publish\OrtakAlan.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Ortak Alan'ı başlat"; Flags: nowait postinstall skipifsilent
