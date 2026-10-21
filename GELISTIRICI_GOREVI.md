# Geliştirici görevi — Şifreli akış için video katmanı

> **Durum: KOD 20.10.2026'da TAMAMLANDI, GERÇEK CİHAZ TESTİ 21.10.2026'da GEÇTİ.** Aşağıdaki görevin tamamı
> uygulandı; derleme 0 hata/0 uyarı, 15/15 test geçiyor, SDK DLL'leri çıktıda. Şifreleme açık cihazda 16 kamera
> SDK yoluyla akıyor (CPU ~%8.7, 32 çekirdeğin yüzdesi). Bu belge artık uygulamanın nasıl kurulduğunu anlatan
> tarihsel bir referanstır; yeni iş vermez. Sıradaki iş (paketleme) için bkz. [CLAUDE_REHBERI.md](CLAUDE_REHBERI.md).

Bu belge, SPVision'da eksik kalan tek parçayı tamamlayacak C# geliştiricisi içindi. Uygulamanın geri
kalanı çalışıyor ve testleri geçiyor; yalnızca Hikvision'ın **akış şifreleme (stream encryption)** açık olduğu
kameralarda görüntü açan katman eksikti.

## Özet

- Dil/çatı: C# / .NET 10 / WPF (x64). Çözüm: `SPVision.slnx`.
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

### 1. Oturum ve yaşam döngüsü — `src/SPVision/Hik/HikSdk.cs`, `HikSession.cs`, `HikException.cs`
- Uygulama açılışında bir kez `NET_DVR_Init()`, kapanışında `NET_DVR_Cleanup()` (App.xaml.cs).
- Cihaz başına tek oturum: `HikSession.GetOrCreate(device)` → `NET_DVR_Login_V40` (port `Device.SdkPort`=8000),
  `Logout` ile çıkış. Kullanıcı adı/şifre `Device.UserName` / `Device.Password`'ten gelir.
- `NET_DVR_SetExceptionCallBack_V30` ile kopmalar yakalanıyor (oynatma bitişi 0x8010 dahil);
  `NET_DVR_SetReconnect(30000, true)` açık. Hata kodları Türkçe açıklamalı: `HikException.Describe(code)`.

### 2. Canlı görüntü — `src/SPVision/Hik/HikLivePlayer.cs` + `Controls/VideoTile`
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

### 3. Kayıt oynatma — `src/SPVision/Hik/HikPlaybackPlayer.cs` + `Views/PlaybackView`
- Kayıt arama ISAPI ile aynı (dokunulmadı). Oynatma: `NET_DVR_PlayBackByTime_V40` +
  `NET_DVR_SetPlayDataCallBack_V40`; PlayCtrl boru hattı aynı ama `StreamFile` kipinde 10 MB tampon.
- Duraklat/sürdür/başlat: `NET_DVR_PlayBackControl_V40` (PlayStart/PlayPause/PlayRestart).
  Konum: `PlayGetTime` → NET_DVR_TIME (6 DWORD). Klip: `NET_DVR_PlayBackSaveData` / `NET_DVR_StopPlayBackSave`.
  Bitiş: exception callback 0x8010 → `Ended` olayı.
- `PlaybackView` çift konak barındırır (FlyleafHost + HikHost, görünürlük takası); zaman çizelgesi konumu
  aktif yoldan okunur (Flyleaf `CurTime` ya da SDK `GetTime()`).

### 4. DLL'ler — `SPVision.csproj`
- `sdk/.../lib/*.dll` → uygulama kökü, `lib/HCNetSDKCom/*.dll` → `HCNetSDKCom\` alt klasörü
  (`None Include` + `CopyToOutputDirectory`), SDK yoksa derleme uyarı verir ama kırılmaz.
  SDK lisansı gereği DLL'ler repoya **konmaz**.

## Kabul ölçütü
- Şifreleme açık cihazda 16 kamera HD açılıyor, görüntü düzgün. *(21.10.2026: geçti — 16 kamera SDK yoluyla, log temiz)*
- İşlemci yükü eski iVMS-4200 Lite'tan belirgin düşük (karşılaştırma için: eski uygulama 4 kamerada
  tek çekirdeğin ~%107'si; yeni uygulama şifresiz akışta 16 kamerada ~%52). *(21.10.2026: geçti — şifreli
  akışta 16 kamera SDK yoluyla CPU ~%8.7, 32 çekirdeğin yüzdesi)*
- Akış anahtarı en fazla bir kez soruluyor (mümkünse hiç: ISAPI'den okunuyor), DPAPI ile saklanıyor.
- Kayıt cihazında hiçbir ayar değiştirilmiyor. *(akış şifreleme açık kaldı, dokunulmadı)*

## Gerçek cihaz testi — 21.10.2026'da geçti

Kayıt cihazında (`192.168.1.2`, DS-8632NI-I8, akış şifreleme açık) yapıldı:

- Canlı izlemede **16 kamera aynı anda SDK yoluyla** açıldı; SDK logu temiz (`Stream Head Data Callback` 16×).
- İşlemci yükü **~%8.7** (32 çekirdeğin yüzdesi) — yukarıdaki referansların belirgin altında.
- Test sırasında bulunan ve aynı gün düzeltilen hata: **SDK kanal numarası.** NVR'larda SDK kanal no ≠ ISAPI
  kanal no; dijital kanallar `StartDChan`'dan başlar (bu cihazda **33**). Yanlış numara `NET_DVR_CHANNEL_ERROR`
  (dwError 4) veriyordu; artık `HikSession.SdkChannel(isapiChannel)` ile çevriliyor.
- Sorun çıkarsa ilk ipuçları hâlâ geçerli: `HikSdk.ExceptionRaised` olayları, `HikException.Describe` çıktıları
  ve `%APPDATA%\SPVision\logs\sdk\` altındaki HCNetSDK logu.

## Nereden başlamalı
`PROJE_DURUMU.md` genel durumu, `reversing/BULGULAR.md` eski uygulamanın nasıl çalıştığını anlatır.
Kod ve gerçek cihaz testi tamamlandı; sıradaki iş **paketleme (yayınlama)** — adımlar
[CLAUDE_REHBERI.md](CLAUDE_REHBERI.md) § 7'de.
