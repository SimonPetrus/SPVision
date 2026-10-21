; SPVision kurulumu — Inno Setup 6 (https://jrsoftware.org/isinfo.php).
;
; Bilgisayar geneli kurulum: C:\Program Files\SPVision, yönetici izniyle. İçerik `dotnet publish` çıktısıdır (kendi içinde
; .NET + Hikvision SDK DLL'leri + FFmpeg DLL'leri). Ayarlar kullanıcıya özeldir (%APPDATA%\SPVision) ve kaldırmada silinmez.
;
; Elle derlenmez; tools\release\release.ps1 (ya da .github\workflows\release.yml) şöyle çağırır:
;   ISCC.exe /DAppVersion=1.2.3 /DPublishDir=<publish klasörü> /DOutputDir=<çıktı klasörü> installer\SPVision.iss
; Çıktı: SPVision-Kurulum-<sürüm>.exe (uygulama içi güncelleyici bu adı manifest.json'dan okur).
;
; Uygulama içi güncelleme şu bağımsız değişkenlerle başlatır (UAC onayıyla, zaten yönetici olarak):
;   /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 /LOG="<geçici klasör>\kurulum.log"
; /RELAUNCH=1 yalnız sessiz kurulumda SPVision'u bittikten sonra yeniden açar — kullanıcının normal (yönetici olmayan)
; haklarıyla: Setup zaten yükseltilmiş başlatıldığı için `runasoriginaluser` işe yaramaz (Inno belgesi: "Setup already
; elevated → no effect"); bu yüzden açma işi explorer.exe üzerinden kullanıcının mevcut kabuğuna devredilir.

#ifndef AppVersion
  #error AppVersion tanımlı değil. tools\release\release.ps1 kullanın (ISCC /DAppVersion=1.2.3 /DPublishDir=... /DOutputDir=...).
#endif
#ifndef PublishDir
  #error PublishDir tanımlı değil (dotnet publish çıktısı).
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
; AppId SABİTTİR: değişirse yeni sürüm eskisinin üstüne değil yanına kurulur ve güncelleme bozulur.
AppId={{A6CEB665-4B8E-42E5-8026-4E1F8D8B1134}
AppName=SPVision
AppVersion={#AppVersion}
AppVerName=SPVision {#AppVersion}
AppPublisher=Simon Petrus
AppCopyright=© 2026 Simon Petrus
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=SPVision kurulumu
DefaultDirName={autopf}\SPVision
DisableDirPage=auto
DefaultGroupName=SPVision
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=SPVision-Kurulum-{#AppVersion}
SetupIconFile=..\src\SPVision\Assets\SPVision.ico
UninstallDisplayIcon={app}\SPVision.exe
UninstallDisplayName=SPVision
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Çalışan SPVision'u Restart Manager ile kapatır; yeniden başlatmayı Restart Manager'a bırakmaz (yükseltilmiş açardı).
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
; Kaldırmada yalnız HKCU\...\Run içindeki "SPVision" değeri silinir (Windows ile başlat).
UsedUserAreasWarning=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Klasör yapısı korunur: FFmpeg\ ve HCNetSDKCom\ alt klasörleri SDK'nın beklediği yerde kalmalı.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SPVision"; Filename: "{app}\SPVision.exe"
Name: "{autodesktop}\SPVision"; Filename: "{app}\SPVision.exe"; Tasks: desktopicon

[Run]
; Etkileşimli kurulum: bitiş sayfasında "SPVision'u başlat" kutusu (postinstall = Setup'ı başlatan kullanıcının haklarıyla).
Filename: "{app}\SPVision.exe"; Description: "{cm:LaunchProgram,SPVision}"; Flags: nowait postinstall skipifsilent
; Uygulama içi güncelleme (sessiz + /RELAUNCH=1): kullanıcının yönetici olmayan kabuğu üzerinden yeniden aç.
Filename: "{win}\explorer.exe"; Parameters: """{app}\SPVision.exe"""; Flags: nowait skipifnotsilent; Check: RelaunchRequested

[Code]
{ Uygulama içi güncelleyiciden mi geldi: sessiz kurulum ve /RELAUNCH=1. }
function RelaunchRequested: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

{ True while another process has the file open (a running SPVision.exe cannot be opened exclusively). }
function IsFileLocked(const FileName: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  try
    Stream := TFileStream.Create(FileName, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

{ Güncelleyici Setup'ı başlattıktan sonra SPVision kendini düzgünce kapatır (akışlar bırakılır, MP4 kayıtları
  tamamlanır; en çok ~11 sn). Dosyalar kullanımdayken kopyalamaya başlamamak için en çok 20 sn beklenir; sonra Restart
  Manager (/CLOSEAPPLICATIONS) devreye girer. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Waited: Integer;
begin
  Result := '';
  if not RelaunchRequested then
    Exit;
  Exe := ExpandConstant('{app}\SPVision.exe');
  Waited := 0;
  while FileExists(Exe) and IsFileLocked(Exe) and (Waited < 20000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

{ Kaldırma: "Windows ile birlikte başlat" değeri artık var olmayan exe'yi göstermesin. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SPVision');
end;
