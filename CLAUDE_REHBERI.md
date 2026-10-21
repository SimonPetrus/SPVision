# SP-2026 Lite — Claude için Tam Rehber

> **Bu belgeye ek olarak `PROJE_DURUMU.md`, `GELISTIRICI_GOREVI.md`, `README.md` ve
> `reversing/BULGULAR.md` de klasördedir.** Bu dosya hepsinin özetidir; ayrıntı için
> ilgili belgeye bak. Kodun tamamı bu klasörde çalışır durumdadır: 0 derleme hatası,
> 15/15 birim testi geçer, **gerçek cihazda (Hikvision DS-8632NI-I8) test edilmiştir.**

---

## 1. Proje ne? (tek paragraf)

Eski **iVMS-4200 Lite** (2019, 32-bit, CPU'yu yoran) izleme uygulamasının yerine yazılmış
hafif bir WPF istemcisi. Hikvision DS-8632NI-I8 kayıt cihazına (192.168.1.2) bağlı 22 IP
kamerayı canlı izler (1/4/9/16 ızgara) ve kayıtları oynatır. **En kritik nokta:** Cihazda
"akış şifreleme" (stream encryption) **açıktır ve KAPATILMAYACAK** — bu, proje sahibinin kesin
istemidir. Şifreli akışı yalnızca Hikvision'ın resmi SDK'sı çözebilir; proje bu yüzden
HCNetSDK + PlayCtrl entegrasyonu içerir.

## 2. Klasördeki ne var?

| Yol | Ne işe yarar |
|---|---|
| `SP2026Lite.slnx` | Çözüm dosyası (`.NET 10` SDK formatı). `dotnet build SP2026Lite.slnx` |
| `src/SP2026Lite.Core/` | Cihazdan bağımsız kütüphane: ISAPI istemcisi, modeller, DPAPI ayar deposu, RTSP adresleri |
| `src/SP2026Lite/` | WPF uygulaması (canlı izleme, oynatma, SDK video katmanı) |
| `tests/SP2026Lite.Core.Tests/` | 15 xUnit testi (`dotnet test tests\SP2026Lite.Core.Tests\SP2026Lite.Core.Tests.csproj`) |
| `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` | **Hikvision resmi SDK (lisanslı, repoya konmaz)** — DLL'ler `lib/`, başlıklar `incEn/`, resmi C# örnekleri `C# demo/`, C++ örnekleri `ClientDemo/`, kılavuzlar `doc/` |
| `ffmpeg/` | FFmpeg 9 paylaşımlı DLL'leri (FlyleafLib bunları kullanır) — `tools/get-ffmpeg.ps1` ile yeniden indirilebilir |
| `reversing/` | Eski iVMS'in tersine mühendislik bulguları (`BULGULAR.md` — CPU sorununun kökeni) |
| `tools/` | `get-ffmpeg.ps1` vb. yardımcı betikler |

## 3. Derleme ve çalıştırma (Windows 11, .NET 10 SDK, x64)

```powershell
cd SPVision
dotnet build SP2026Lite.slnx          # 0 hata / 0 uyarı beklenir
dotnet test tests\SP2026Lite.Core.Tests\SP2026Lite.Core.Tests.csproj   # 15/15 geçer
dotnet run --project src\SP2026Lite
```

- SDK ve FFmpeg DLL'leri `.csproj` tarafından **otomatik çıktı klasörüne kopyalanır**;
  `sdk\...\lib` ya da `ffmpeg` klasörü yoksa derleme uyarı verir ama kırılmaz.
- Uygulama ayarları + loglar: `%APPDATA%\SP-2026 Lite\` (`settings.json` — şifreler ve akış
  anahtarı DPAPI ile `dpapi:` önekiyle şifreli; `logs\sdk\SdkLog_1_W.log` = HCNetSDK logu,
  `logs\player.log` = Flyleaf logu).

## 4. Mimari: video iki yoldan akar

```
                 akış anahtarı girili mi? ──evet──► SDK YOLU (şifreli akış)
                        │ hayır                      HikLivePlayer / HikPlaybackPlayer
                        ▼                           HCNetSDK + PlayCtrl boru hattı
                 FLYLEAF YOLU (şifresiz)
                 FlyleafLib + FFmpeg GPU decode
```

- Karar [`VideoTile.Open`](src/SP2026Lite/Controls/VideoTile.xaml.cs) içinde:
  `useHik = _preferHik || Device.StreamKeyFor(Camera) dolu`. FFmpeg şifreli akışı açamaz
  (`Status.Failed`) → **tek seferlik** SDK'ya düşer (`_triedHikFallback`, `_preferHik` kalıcı).
- **SDK boru hattı** (her iki canlı/oynatma için aynı mantık):
  1. `NET_DVR_Login_V40` (cihaz başına tek oturum: [`HikSession`](src/SP2026Lite/Hik/HikSession.cs))
  2. `NET_DVR_RealPlay_V40` (canlı) / `NET_DVR_PlayBackByTime_V40` (oynatma) + veri callback'i
  3. İlk `NET_DVR_SYSHEAD` paketinde: `PlayM4_GetPort` → `PlayM4_SetSecretKey(port, 1, anahtar, 128)`
     → `PlayM4_SetDecodeEngineEx(port, 1)` (GPU) → `PlayM4_SetStreamOpenMode` →
     `PlayM4_OpenStream` (canlı 2 MB / oynatma 10 MB) → `SetDisplayBuf(15)` → `PlayM4_Play(port, hwnd)`
  4. `NET_DVR_STREAMDATA` → `PlayM4_InputData`
- **ÇOK ÖNEMLİ — kanal numarası:** NVR'larda SDK kanal no ≠ ISAPI kanal no. Dijital kanallar
  `DeviceInfoV40.DeviceV30.StartDChan`'dan başlar (bu cihazda **33**). **Mutlaka**
  [`HikSession.SdkChannel(isapiChannel)`](src/SP2026Lite/Hik/HikSession.cs) ile çevir; yanlış
  numara `NET_DVR_CHANNEL_ERROR` (dwError 4) verir ve cihaz linki reddeder. (Bu hata 21.10.2026'da
  bulundu ve düzeltildi; logda `LinkToDvr ... dwErrCode[0x00100100]` görürsen kanal sorunu var.)
- SDK görüntüyü bir **HWND**'ye çizer (WPF'te airspace): [`HikVideoHost`](src/SP2026Lite/Controls/HikVideoHost.cs)
  (HwndHost) video yüzeyidir. Kare üstü yazılar (kamera adı, KAYIT rozeti) HWND'ye çizilemez →
  VideoTile bir **Popup** kullanır; PlaybackView Flyleaf overlay'ini gizleyip StatusText kullanır.

## 5. Klasör → dosya haritası (WPF tarafı, yollar `src/SP2026Lite/` altına göredir)

| Dosya | Sorumluluk |
|---|---|
| `App.xaml.cs` | `HikSdk.Init/Shutdown`, Flyleaf `Engine.Start`, ayar yükleme |
| `MainWindow.xaml(.cs)` | Pencere, görünüm değiştirme (Canlı İzleme/Kayıt Oynatma), cihaz ağacı (ekle/düzenle/sil, sürükle-bırak), tam ekran (F11) |
| `Views/LiveView.xaml(.cs)` | 1/4/9/16 ızgara, sürükle-bırak, düzeni kaydet/geri yükle |
| `Controls/VideoTile.xaml(.cs)` | Tek kamera karesi — **çift yol** (Flyleaf/SDK), Popup overlay, yeniden bağlanma (3/10/30 sn), kayıt + anlık görüntü |
| `Views/PlaybackView.xaml(.cs)` | Kayıt oynatma — ISAPI arama + zaman çizelgesi + çift yol oynatma (oynat/duraklat/sar/klip) |
| `Views/DeviceDialog.xaml(.cs)` | Cihaz ekle/düzenle: IP, port, kullanıcı, şifre, **akış anahtarı (doğrulama kodu)** + "Cihazdan oku" butonu (ISAPI'den çeker) |
| `Controls/TimelineBar.cs` | 24 saatlik kayıt zaman çizelgesi |
| `Hik/HikNative.cs` | Tüm P/Invoke imzaları + struct'lar (`incEn` başlıklarıyla satır satır doğrulandı) |
| `Hik/HikSdk.cs` | Init/Cleanup, exception callback, `Available` |
| `Hik/HikSession.cs` | Cihaz başına tek login; `SdkChannel()` çevirisi |
| `Hik/HikLivePlayer.cs` | Canlı SDK oturumu (RealPlay + PlayCtrl) |
| `Hik/HikPlaybackPlayer.cs` | Oynatma SDK oturumu (PlayBackByTime + PlayCtrl + GetTime + klip SaveData) |
| `Hik/HikException.cs` | SDK hata kodlarının Türkçe açıklamaları (`Describe`) |

Core tarafı: [`IsapiClient`](src/SP2026Lite.Core/IsapiClient.cs) (Digest auth; cihaz bilgisi,
kamera listesi, kayıt arama, anlık görüntü, şifreleme durumu + doğrulama kodu okuma),
[`Models.cs`](src/SP2026Lite.Core/Models.cs) (Device/Camera/AppSettings; `StreamKeyFor()`:
kamera anahtarı yoksa cihaz anahtarını kullanır), [`SettingsStore`](src/SP2026Lite.Core/SettingsStore.cs)
(DPAPI şifreli JSON), [`StreamUrls`](src/SP2026Lite.Core/StreamUrls.cs) (RTSP adresleri).

## 6. Doğrulanmış gerçek durum (21.10.2026)

- Cihaz: DS-8632NI-I8, 192.168.1.2, portlar 80/554/8000, **akış şifreleme AÇIK** (dokunulmadı).
- **16 kamera aynı anda SDK yoluyla akıyor**, log temiz (`Stream Head Data Callback` 16×),
  **CPU ~%8.7** (32 çekirdeğin yüzdesi; eski iVMS 4 kamerada tek çekirdeğin %107'siydi).
- Şifresiz Flyleaf yolu daha önce test edildi: 16 kamera HD'de tek çekirdek ~%52.
- Derleme 0 hata/0 uyarı, 15/15 test. Kalan tek iş: **paketleme (yayınlama)**.

## 7. Senin (Claude'un) yapacağın iş: paketleme

Hedef: çift tıklayınca çalışan, kurulum gerektirmeyen bir klasör/exe.

1. **Yayınlama komutu** (önerilen başlangıç):
   ```powershell
   dotnet publish src\SP2026Lite\SP2026Lite.csproj -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=false -o publish
   ```
   - `self-contained` sayede kullanıcı makinesinde .NET gerekmez (~150 MB eklenir).
     Daha küçük istenirse `--self-contained false` + framework-dependent kurulum.
   - Çıktıda olması gerekenler: `SP-2026 Lite.exe`, `FFmpeg\*.dll`, `HCNetSDK.dll`,
     `PlayCtrl.dll`, `HCCore.dll`, `HCNetSDKCom\` alt klasörü (16 DLL) — csproj bunları
     otomatik kopyalar; publish sonrası **kontrol et**.
2. **Dikkat:** SDK DLL'leri lisanslıdır; ihraç edilen paketin içinde olabilirler (kullanım
   içindir) ama **repoya/internete yüklenmez**.
3. Tek dosya exe (`PublishSingleFile=true`) denenecekse: yerel DLL'ler (`HCNetSDK`,
   `PlayCtrl`) `IncludeNativeLibrariesForSelfExtract=true` gerektirir; HCNetSDKCom alt
   klasör yapısı **korunmalıdır** (SDK runtime'da `HCNetSDKCom\*.dll` arar) — bu yüzden
   **klasör dağıtımı + Inno Setup/MSIX zip tercih edilir**.
4. Yayın sonrası el ile test: boş `%APPDATA%\SP-2026 Lite` ile ilk açılış (cihaz ekleme
   sihirbazı), 16 kamera canlı, oynatma, anlık görüntü, klip.

## 8. Sık karşılaşılacak tuzaklar (öğrenilmiş dersler)

- **wpftmp derleme geçişi** `ImplicitUsings`'i düşürür → `File`/`Directory` gibi tipleri
  `System.IO.` ile nitelendir; `error CS0103` görürsen önce bunu kontrol et.
- XAML'de `"` içeren satırlarda arama/değiştirme yapacaksan entity sorun çıkarır;
  minimal satır bazlı düzenleme kullan.
- SDK hata kodlarının anlamı: `HikException.Describe(code)` + `logs\sdk\SdkLog_1_W.log`
  (en değerli teşhis kaynağı; `dwError[4]`=kanal hatası, `0x8010`=oynatma sonu vb.).
- SDK API çağrıları **bloklayan** ağ çağrılarıdır → UI thread'inde çağırma (mevcut kod
  `Task.Run` + `_openGeneration` yarış koruması kullanıyor; bu deseni bozma).
- Kamera kanalları her yerde ISAPI numarasıyla saklanır (1-22); SDK'ya girmeden
  `SdkChannel()` ile çevrilir. Flyleaf/RTSP yolu ISAPI numarasını doğrudan kullanır.
- Cihaz ayarları **asla değiştirilmez** (şifreleme/Hik-Connect açık kalır) — bu, sahibinin
  kesin kuralıdır; ISAPI'de yalnızca **GET** çağrıları kullanılır.
