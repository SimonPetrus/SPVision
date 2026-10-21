# SP-2026 Lite — Proje Durumu

Son güncelleme: 21.10.2026 (gerçek cihaz testi geçti; sıradaki iş paketleme)

Bu belge, projeye kim devam ederse etsin (yeni bir sohbet ya da bir geliştirici) her şeyi baştan anlatmaya
gerek kalmasın diye tutuluyor. Ayrıntılı ve en güncel rehber: [CLAUDE_REHBERI.md](CLAUDE_REHBERI.md).

## Amaç

Eski **iVMS-4200 Lite** (2019, 32-bit) yerine hafif, işlemciyi yormayan ve kamera akış anahtarını bir kez
sorup hatırlayan bir izleme uygulaması. **Kayıt cihazında hiçbir güvenlik ayarı kapatılmayacak**
(akış şifreleme ve Hik-Connect açık kalacak). Bu, proje sahibinin kesin isteğidir.

## Ortam

| | |
|---|---|
| Kayıt cihazı | Hikvision DS-8632NI-I8, `192.168.1.2`, 22 IP kamera, H.265 |
| Akışlar | Ana: 1920×1080 @ 15 fps · Alt: 1280×720 @ 25 fps |
| Portlar | 80 (ISAPI, Digest), 554 (RTSP), 8000 (SDK) |
| Akış şifreleme | **Açık** (`/ISAPI/System/Network/EZVIZ` → `streamEncrypteEnabled=true`) |
| Bilgisayar | Windows 11, RTX 4070 Laptop + Intel UHD, 32 çekirdek |
| Araçlar | .NET 10 SDK, FFmpeg 9, Ghidra 12.1.4 (`Desktop\ghidra_12.1.4_PUBLIC`) |

## Bitenler

- **Tersine mühendislik:** Eski uygulamanın mimarisi ve CPU yükünün kaynağı → [reversing/BULGULAR.md](reversing/BULGULAR.md)
- **Core kütüphanesi** (`src/SP2026Lite.Core`): ISAPI istemcisi (cihaz bilgisi, kamera listesi, kayıt arama,
  anlık görüntü, akış şifreleme durumu + doğrulama kodunu okuma), RTSP adresleri, DPAPI ile şifreli ayar deposu, ızgara düzenleri (`GridLayout`). 32 birim testi.
- **WPF arayüzü** (`src/SP2026Lite`): 6/8/25/32 ızgara (6/8: büyük kare + küçükler), sürükle-bırak, çift tıkla büyüt, tam ekran,
  HD/SD/Oto kalite, otomatik yeniden bağlanma, kayıt oynatma + zaman çizelgesi, anlık görüntü, MP4 kayıt,
  cihaz ekleme/düzenleme.
- **Video katmanı (şifresiz akışlar için):** FlyleafLib + FFmpeg 9, GPU decode. Şifreleme kapalıyken
  test edildi: 16 kamera HD'de CPU tek çekirdeğin ~%52'si (eski uygulama 4 kamerada %107).
- **SDK video katmanı (şifreli akışlar için) — 20.10.2026'da tamamlandı:** Hikvision Device Network SDK
  V6.1.11.40 (win64) entegrasyonu, 0 derleme hatası, 15/15 test geçiyor:
  - `src/SP2026Lite/Hik/HikNative.cs` — tüm P/Invoke imzaları `incEn/HCNetSDK.h` ve `plaympeg4.h`
    başlıklarıyla satır satır doğrulandı (Login_V40, RealPlay_V40, PlayBackByTime_V40, SaveRealData_V30,
    PlayCtrl ailesi, exception callback vb.).
  - `src/SP2026Lite/Hik/HikSdk.cs` + `HikSession.cs` — Init/Cleanup yaşam döngüsü, cihaz başına tek oturum
    (Login_V40, port `Device.SdkPort`=8000), 30 sn otomatik yeniden bağlanma, Türkçe hata açıklamaları
    (`HikException.Describe`).
  - `src/SP2026Lite/Hik/HikLivePlayer.cs` — canlı görüntü: RealPlay_V40 veri callback'i → PlayCtrl
    boru hattı (`GetPort → SetSecretKey(1,key,128) → SetDecodeEngineEx(HARD=GPU) → OpenStream(2 MB) →
    SetDisplayBuf(15) → SetOverlayMode(false) → Play(port,hwnd)`). MP4 kayıt `SaveRealData_V30`,
    anlık görüntü `PlayM4_GetJPEG` (8 MB).
  - `src/SP2026Lite/Hik/HikPlaybackPlayer.cs` — kayıt oynatma: `PlayBackByTime_V40` +
    `SetPlayDataCallBack_V40`, StreamFile kipinde 10 MB tampon; duraklat/sürdür `PlayBackControl_V40`,
    konum `PlayGetTime` (NET_DVR_TIME), klip `NET_DVR_PlayBackSaveData`.
  - `src/SP2026Lite/Controls/HikVideoHost.cs` — HwndHost tabanlı video yüzeyi (SDK bir HWND'ye çizer).
  - **Çift yollu arayüz:** `VideoTile` (canlı) ve `PlaybackView` (oynatma) iki konak barındırır
    (FlyleafHost + HikHost). Kamera için akış anahtarı varsa (`Device.StreamKeyFor`) ya da FFmpeg
    şifreli akışta başarısız olursa (tek seferlik fallback) SDK yolu kullanılır; aksi halde Flyleaf.
    VideoTile'da kare üstü yazılar `Popup` ile videonun üstünde durur (HWND airspace sorunu).
  - SDK DLL'leri `SP2026Lite.csproj` ile çıktıya kopyalanır (`sdk/.../lib` → uygulama kökü,
    `HCNetSDKCom` alt klasörü korunur; SDK yoksa derleme uyarı verir, kırılmaz).

- **Gerçek cihaz testi — 21.10.2026'da geçti** (DS-8632NI-I8, akış şifreleme **açık**, dokunulmadı):
  - **16 kamera aynı anda SDK yoluyla** akıyor; SDK logu temiz (`Stream Head Data Callback` 16×).
  - İşlemci yükü **~%8.7** (32 çekirdeğin yüzdesi; eski iVMS 4 kamerada tek çekirdeğin %107'siydi).
- **SDK kanal numarası hatası — 21.10.2026'da bulundu ve düzeltildi:** NVR'larda SDK kanal numarası ISAPI
  kanal numarasından farklıdır; dijital kanallar `StartDChan`'dan başlar (bu cihazda **33**). Yanlış numara
  `NET_DVR_CHANNEL_ERROR` (dwError 4) veriyordu. Artık her SDK çağrısından önce
  `HikSession.SdkChannel(isapiChannel)` ile çevriliyor; kanallar uygulamada ISAPI numarasıyla (1-22) saklanır.

## Kalan iş: paketleme (yayınlama)

Kod ve cihaz testi bitti; kalan tek iş, çift tıklayınca çalışan, kurulum gerektirmeyen bir paket üretmek
(ayrıntılı adımlar: [CLAUDE_REHBERI.md](CLAUDE_REHBERI.md) § 7):

- `dotnet publish` ile `win-x64` yayını (self-contained önerilir; kullanıcı makinesinde .NET gerekmez).
- Çıktıda `SP-2026 Lite.exe`, `FFmpeg\*.dll`, `HCNetSDK.dll`, `PlayCtrl.dll`, `HCCore.dll` ve `HCNetSDKCom\`
  alt klasörünün bulunduğunu kontrol et (SDK runtime'da `HCNetSDKCom\*.dll` arar; klasör yapısı korunmalı).
- Tek dosya exe yerine klasör dağıtımı (+ Inno Setup/MSIX ya da zip) tercih edilir.
- Yayın sonrası el ile test: boş `%APPDATA%\SP-2026 Lite` ile ilk açılış (cihaz ekleme), 16 kamera canlı,
  oynatma, anlık görüntü, klip.
- SDK DLL'leri lisanslıdır: paketin içinde olabilirler (kullanım içindir) ama repoya/internete **yüklenmez**.

## Notlar

- Kayıt cihazında 20.10.2026'da akış şifreleme kısa süre kapatılıp test edildi, aynı gün **geri açıldı**.
- SDK lisansı gereği SDK DLL'leri repoya **konmaz**; `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/`
  klasörü `.gitignore`'da.
- Eski iVMS için geçici rahatlama: Sistem Yapılandırması'nda "Donanım kod çözme"yi açmak ve cihaz düzenleme
  ekranında akış anahtarını kaydetmek.

## Derleme

SDK önce el ile `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` klasörüne yerleştirilmelidir (repoda yok).

```powershell
.\tools\get-ffmpeg.ps1
dotnet build SP2026Lite.slnx
dotnet test tests\SP2026Lite.Core.Tests\SP2026Lite.Core.Tests.csproj
dotnet run --project src\SP2026Lite
```
