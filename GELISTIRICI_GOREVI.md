# Geliştirici görevi — Şifreli akış için video katmanı

> **Durum: 20.10.2026 itibarıyla KOD TAMAMLANDI.** Aşağıdaki görevin tamamı uygulandı; derleme 0 hata/0 uyarı,
> 15/15 test geçiyor, SDK DLL'leri çıktıda. Kalan tek iş gerçek cihazda test (bkz. son bölüm). Bu belge artık
> uygulamanın nasıl kurulduğunu anlatan referanstır; yeni iş vermez.

Bu belge, SP-2026 Lite'ta eksik kalan tek parçayı tamamlayacak C# geliştiricisi içindi. Uygulamanın geri
kalanı çalışıyor ve testleri geçiyor; yalnızca Hikvision'ın **akış şifreleme (stream encryption)** açık olduğu
kameralarda görüntü açan katman eksikti.

## Özet

- Dil/çatı: C# / .NET 10 / WPF (x64). Çözüm: `SP2026Lite.slnx`.
- Mevcut video katmanı FFmpeg (FlyleafLib) ile **şifresiz** RTSP akışlarını GPU'da çözüyor. Çalışıyor.
- FFmpeg, Hikvision'ın şifreli akışını açamıyor. Şifreli kameralar için Hikvision'ın resmi
  **Device Network SDK** (HCNetSDK + PlayCtrl) kullanıldı.
- Kayıt cihazında güvenlik ayarları **değiştirilmedi** (şifreleme ve Hik-Connect açık kaldı).

## Eldeki malzeme (referans)

| Ne | Nerede |
|---|---|
| Resmi SDK (lisanslı, repoda yok) | `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` |
| SDK — canlı önizleme C# örneği | `sdk/.../C# demo/2-Preview-NVRCsharpDemo/NVRCsharpDemo/MainWindow.cs` |
| SDK — oynatma + indirme C# örneği | `sdk/.../C# demo/3-playback&doanload/` |
| SDK — şifreli akış C++ örneği (SetSecretKey) | `sdk/.../ClientDemo/DlgPlayRemoteTime.cpp` (`PlayM4_SetSecretKey(lPort, 1, key, 128)`) |
| SDK — donanım decode | `PlayM4_SetDecodeEngineEx(port, 1)` (1 = HARD_DECODE_ENGINE) |
| SDK başlıkları | `sdk/.../incEn/HCNetSDK.h`, `plaympeg4.h` |
| Akış anahtarının cihazdan okunması | `IsapiClient.GetStreamEncryptionAsync()` → `StreamEncryptionInfo(Enabled, VerificationCode)` |
| Anahtarın saklanması | `Device.StreamKey` / `Camera.StreamKey`, DPAPI ile şifreli (`SettingsStore`) |

## Yapılmış iş (karşı karşılığı)

### 1. Oturum ve yaşam döngüsü — `src/SP2026Lite/Hik/HikSdk.cs`, `HikSession.cs`, `HikException.cs`
- Uygulama açılışında bir kez `NET_DVR_Init()`, kapanışında `NET_DVR_Cleanup()` (App.xaml.cs).
- Cihaz başına tek oturum: `HikSession.GetOrCreate(device)` → `NET_DVR_Login_V40` (port `Device.SdkPort`=8000),
  `Logout` ile çıkış. Kullanıcı adı/şifre `Device.UserName` / `Device.Password`'ten gelir.
- `NET_DVR_SetExceptionCallBack_V30` ile kopmalar yakalanıyor (oynatma bitişi 0x8010 dahil);
  `NET_DVR_SetReconnect(30000, true)` açık. Hata kodları Türkçe açıklamalı: `HikException.Describe(code)`.

### 2. Canlı görüntü — `src/SP2026Lite/Hik/HikLivePlayer.cs` + `Controls/VideoTile`
- `NET_DVR_RealPlay_V40` veri callback'i: `NET_DVR_SYSHEAD` → PlayCtrl boru hattı
  (`GetPort → SetSecretKey(1, key, 128) → SetDecodeEngineEx(HARD=GPU) → OpenStream(2 MB) →
  SetDisplayBuf(15) → SetOverlayMode(false) → Play(port, hwnd)`), `NET_DVR_STREAMDATA` →
  `PlayM4_InputData` (50 kez `Sleep(2)` ile retry).
- `Controls/HikVideoHost.cs`: HwndHost tabanlı yüzey; SDK görüntüyü bu HWND'ye çizer.
- Kare üstü yazılar (kamera adı, durum, KAYIT rozeti) `Popup` ile videonun üstünde tutulur
  (HWND airspace); kök pencere taşınınca/durunca Popup `HorizontalOffset` küçük oynamayla yeniden hizalanır.
- HD/SD: `PreviewInfo.StreamType` 0/1, mevcut `StreamQuality` enum'una bağlı.
- Anlık görüntü: `PlayM4_GetJPEG` (8 MB tampon); önce ISAPI snapshot denenir. Yerel kayıt:
  `NET_DVR_SaveRealData_V30` (TransType = MP4).
- Çift yol seçimi: kamera için akış anahtarı varsa (`Device.StreamKeyFor`) ya da FFmpeg `Status.Failed`
  olursa (tek seferlik fallback, `_preferHik` kalıcı) SDK yolu; aksi halde Flyleaf. Aç/kapa yarışları
  `_openGeneration` sayaçıyla iptal edilir; oturum + `Start` arka planda `Task.Run` ile çalışır.

### 3. Kayıt oynatma — `src/SP2026Lite/Hik/HikPlaybackPlayer.cs` + `Views/PlaybackView`
- Kayıt arama ISAPI ile aynı (dokunulmadı). Oynatma: `NET_DVR_PlayBackByTime_V40` +
  `NET_DVR_SetPlayDataCallBack_V40`; PlayCtrl boru hattı aynı ama `StreamFile` kipinde 10 MB tampon.
- Duraklat/sürdür/başlat: `NET_DVR_PlayBackControl_V40` (PlayStart/PlayPause/PlayRestart).
  Konum: `PlayGetTime` → NET_DVR_TIME (6 DWORD). Klip: `NET_DVR_PlayBackSaveData` / `NET_DVR_StopPlayBackSave`.
  Bitiş: exception callback 0x8010 → `Ended` olayı.
- `PlaybackView` çift konak barındırır (FlyleafHost + HikHost, görünürlük takası); zaman çizelgesi konumu
  aktif yoldan okunur (Flyleaf `CurTime` ya da SDK `GetTime()`).

### 4. DLL'ler — `SP2026Lite.csproj`
- `sdk/.../lib/*.dll` → uygulama kökü, `lib/HCNetSDKCom/*.dll` → `HCNetSDKCom\` alt klasörü
  (`None Include` + `CopyToOutputDirectory`), SDK yoksa derleme uyarı verir ama kırılmaz.
  SDK lisansı gereği DLL'ler repoya **konmaz**.

## Kabul ölçütü
- Şifreleme açık cihazda 16 kamera HD açılıyor, görüntü düzgün. *(kod hazır — test bekliyor)*
- İşlemci yükü eski iVMS-4200 Lite'tan belirgin düşük (karşılaştırma için: eski uygulama 4 kamerada
  tek çekirdeğin ~%107'si; yeni uygulama şifresiz akışta 16 kamerada ~%52).
- Akış anahtarı en fazla bir kez soruluyor (mümkünse hiç: ISAPI'den okunuyor), DPAPI ile saklanıyor.
- Kayıt cihazında hiçbir ayar değiştirilmiyor.

## Kalan iş: gerçek cihaz testi

Kayıt cihazı (`192.168.1.2`) erişilebilirken:

1. `dotnet run --project src\SP2026Lite` ile aç, canlı izlemede 16 kamera HD aç; görüntü, donma, çökme kontrolü.
2. Görev Yöneticisi'nden işlemci yükünü ölç (hedef yukarıdaki referansların altında).
3. Akış anahtarı sorulmuyorsa ISAPI okuma çalışıyor demektir; sorulursa bir kez gir, sonrası DPAPI'den gelmeli.
4. Oynatma ekranında gün/kamera seç; oynat, duraklat, sar, anlık görüntü, klip al.
5. Sorun çıkarsa: `HikSdk.ExceptionRaised` olayları ve `HikException.Describe` çıktıları ilk ipucudur;
   `HikNative.cs` imzaları `incEn` başlıklarıyla satır satır doğrulanmıştır, sapma varsa önce çağrı
   parametrelerini (channel, port, hWnd, anahtar uzunluğu 128) kontrol edin.

## Nereden başlamalı
`PROJE_DURUMU.md` genel durumu, `reversing/BULGULAR.md` eski uygulamanın nasıl çalıştığını anlatır.
Kod tarafı tamamlandı; sıra yukarıdaki gerçek cihaz testinde.
