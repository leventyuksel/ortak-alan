#ifndef MyAppVersion
  #define MyAppVersion "2.0.4"
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

[Code]
function RunFirewall(Remove: Boolean): Boolean;
var
  Exe, Params: String;
  Code: Integer;
begin
  Exe := ExpandConstant('{app}\{#MyAppExeName}');
  if not FileExists(Exe) then
  begin
    Log('Güvenlik duvarı: program dosyası yok.');
    Result := False;
    Exit;
  end;
  if Remove then
    Params := '--guvenlik-duvari --kaldir'
  else
    Params := '--guvenlik-duvari';
  Code := 1;
  if IsAdminInstallMode then
    Result := Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code)
  else
    Result := ShellExec('runas', Exe, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code);
  if Result then
    Result := Code = 0;
  Log('Güvenlik duvarı çıkış kodu: ' + IntToStr(Code));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  if (not IsAdminInstallMode) and (not WizardSilent) then
    MsgBox('Diğer bilgisayarların bu bilgisayarı görebilmesi için Ortak Alan, Windows güvenlik duvarına eklenecek. Açılan pencerede Evet''e basın.', mbInformation, MB_OK);
  if not RunFirewall(False) then
    if not WizardSilent then
      MsgBox('Güvenlik duvarına eklenemedi. Kurulumu yeniden çalıştırıp izin penceresinde Evet''e basın.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  if (not IsAdminInstallMode) and (not UninstallSilent) then
    MsgBox('Güvenlik duvarındaki Ortak Alan izni kaldırılacak. Açılan pencerede Evet''e basın.', mbInformation, MB_OK);
  RunFirewall(True);
end;
