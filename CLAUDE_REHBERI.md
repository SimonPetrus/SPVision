# SPVision — Claude için Tam Rehber

> **Bu belgeye ek olarak `PROJE_DURUMU.md`, `GELISTIRICI_GOREVI.md`, `README.md` ve
> `reversing/BULGULAR.md` de klasördedir.** Bu dosya hepsinin özetidir; ayrıntı için
> ilgili belgeye bak. Kodun tamamı bu klasörde çalışır durumdadır: 0 derleme hatası,
> 118/118 birim testi geçer, **gerçek cihazda (Hikvision DS-8632NI-I8) test edilmiştir.**
> (21.10.2026'daki düzen seçici + eş zamanlı çoklu oynatma değişikliği henüz cihazda denenmedi; bkz. §4.)
>
> **Ad değişikliği (21.10.2026):** Uygulamanın eski adı **SP-2026 Lite** idi (proje/namespace
> `SP2026Lite`, exe `SP-2026 Lite.exe`). Sahibinin onayıyla adı **SPVision** oldu; aşağıdaki
> geçmiş notlarında eski ad geçiyorsa aynı uygulamadır.

---

## 1. Proje ne? (tek paragraf)

Eski **iVMS-4200 Lite** (2019, 32-bit, CPU'yu yoran) izleme uygulamasının yerine yazılmış
hafif bir WPF istemcisi. Hikvision DS-8632NI-I8 kayıt cihazına (192.168.1.2) bağlı 22 IP
kamerayı canlı izler (1–36 kareli kare ızgaralar) ve kayıtları eş zamanlı (en çok 16 kamera) oynatır. **En kritik nokta:** Cihazda
"akış şifreleme" (stream encryption) **açıktır ve KAPATILMAYACAK** — bu, proje sahibinin kesin
istemidir. Şifreli akışı yalnızca Hikvision'ın resmi SDK'sı çözebilir; proje bu yüzden
HCNetSDK + PlayCtrl entegrasyonu içerir.

## 2. Klasördeki ne var?

| Yol | Ne işe yarar |
|---|---|
| `SPVision.slnx` | Çözüm dosyası (`.NET 10` SDK formatı). `dotnet build SPVision.slnx` |
| `src/SPVision.Core/` | Cihazdan bağımsız kütüphane: ISAPI istemcisi, modeller, DPAPI ayar deposu, RTSP adresleri |
| `src/SPVision/` | WPF uygulaması (canlı izleme, oynatma, SDK video katmanı) |
| `tests/SPVision.Core.Tests/` | 118 xUnit testi (`dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj`) |
| `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` | **Hikvision resmi SDK (lisanslı, repoya konmaz)** — DLL'ler `lib/`, başlıklar `incEn/`, resmi C# örnekleri `C# demo/`, C++ örnekleri `ClientDemo/`, kılavuzlar `doc/` |
| `ffmpeg/` | FFmpeg 9 paylaşımlı DLL'leri (FlyleafLib bunları kullanır) — `tools/get-ffmpeg.ps1` ile yeniden indirilebilir |
| `reversing/` | Eski iVMS'in tersine mühendislik bulguları (`BULGULAR.md` — CPU sorununun kökeni) |
| `tools/` | `get-ffmpeg.ps1` vb. yardımcı betikler |

## 3. Derleme ve çalıştırma (Windows 11, .NET 10 SDK, x64)

```powershell
cd SPVision
dotnet build SPVision.slnx          # 0 hata / 0 uyarı beklenir
dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj   # 118/118 geçer
dotnet run --project src\SPVision
```

- SDK ve FFmpeg DLL'leri `.csproj` tarafından **otomatik çıktı klasörüne kopyalanır**;
  `sdk\...\lib` ya da `ffmpeg` klasörü yoksa derleme uyarı verir ama kırılmaz.
- Uygulama ayarları + loglar: `%APPDATA%\SPVision\` (`settings.json` — şifreler ve akış
  anahtarı DPAPI ile `dpapi:` önekiyle şifreli; `logs\sdk\SdkLog_1_W.log` = HCNetSDK logu,
  `logs\player.log` = Flyleaf logu). Anlık görüntüler `Resimler\SPVision`, kayıt/klipler
  `Videolar\SPVision` altına gider.
- **Eski addan geçiş** ([`LegacyMigration`](src/SPVision.Core/LegacyMigration.cs), `App.OnStartup`):
  `%APPDATA%\SPVision\settings.json` yoksa ve `%APPDATA%\SP-2026 Lite\settings.json` varsa
  dosya **bir kez kopyalanır** (taşınmaz, eski klasör silinmez). Ayarlardaki görüntü/kayıt klasörü
  hâlâ eski varsayılansa (`Resimler\SP-2026 Lite`, `Videolar\SP-2026 Lite`) yeni klasöre çevrilir;
  eski dosyalar yerinde kalır. **DPAPI entropisi `"SP-2026 Lite"` olarak kalmalıdır**
  (`SettingsStore.Entropy`): eski şifre/akış anahtarları onunla şifreli; değiştirirsen hepsi çözülemez.

## 4. Mimari: video iki yoldan akar

```
                 akış anahtarı girili mi? ──evet──► SDK YOLU (şifreli akış)
                        │ hayır                      HikLivePlayer / HikPlaybackPlayer
                        ▼                           HCNetSDK + PlayCtrl boru hattı
                 FLYLEAF YOLU (şifresiz)
                 FlyleafLib + FFmpeg GPU decode
```

- Karar [`VideoTile.Open`](src/SPVision/Controls/VideoTile.xaml.cs) içinde:
  `useHik = _preferHik || Device.StreamKeyFor(Camera) dolu`. FFmpeg şifreli akışı açamaz
  (`Status.Failed`) → **tek seferlik** SDK'ya düşer (`_triedHikFallback`, `_preferHik` kalıcı).
- **SDK boru hattı** (her iki canlı/oynatma için aynı mantık):
  1. `NET_DVR_Login_V40` (cihaz başına tek oturum: [`HikSession`](src/SPVision/Hik/HikSession.cs))
  2. `NET_DVR_RealPlay_V40` (canlı) / `NET_DVR_PlayBackByTime_V40` (oynatma) + veri callback'i
  3. İlk `NET_DVR_SYSHEAD` paketinde: `PlayM4_GetPort` → `PlayM4_SetSecretKey(port, 1, anahtar, 128)`
     → `PlayM4_SetDecodeEngineEx(port, 1)` (GPU) → `PlayM4_SetStreamOpenMode` →
     `PlayM4_OpenStream` (canlı 2 MB / oynatma 10 MB) → `SetDisplayBuf(15)` → `PlayM4_Play(port, hwnd)`
  4. `NET_DVR_STREAMDATA` → `PlayM4_InputData`
- **ÇOK ÖNEMLİ — kanal numarası:** NVR'larda SDK kanal no ≠ ISAPI kanal no. Dijital kanallar
  `DeviceInfoV40.DeviceV30.StartDChan`'dan başlar (bu cihazda **33**). **Mutlaka**
  [`HikSession.SdkChannel(isapiChannel)`](src/SPVision/Hik/HikSession.cs) ile çevir; yanlış
  numara `NET_DVR_CHANNEL_ERROR` (dwError 4) verir ve cihaz linki reddeder. (Bu hata 21.10.2026'da
  bulundu ve düzeltildi; logda `LinkToDvr ... dwErrCode[0x00100100]` görürsen kanal sorunu var.)
- SDK görüntüyü bir **HWND**'ye çizer (WPF'te airspace): [`HikVideoHost`](src/SPVision/Controls/HikVideoHost.cs)
  (HwndHost) video yüzeyidir. Video üstüne WPF içeriği çizilemez → VideoTile **iki satırlı**dır:
  üstte WPF **başlık şeridi** (durum noktası, kamera adı, "Bağlanıyor…", KAYIT ve HD/SD rozeti;
  canlıda 16+ karede, oynatmada 9+ karede 22 px, daha azında 26 px — bir düzende tek yükseklik), altta
  üzerine hiçbir şey çizilmeyen video alanı. (Eski Popup overlay kaldırıldı: üst düzey pencere olduğu için
  pencere taşınınca/küçültülünce masaüstünde asılı kalıyordu — kare üstüne Popup'ı geri getirme. Düzen
  seçici ve "Saate git" ise **geçici** Popup'tır: [`TransientPopup`](src/SPVision/Controls/TransientPopup.cs)
  StaysOpen=false + Esc + pencere devre dışı/taşı/boyutlandır/küçült → kapanır.) `HikVideoHost` `WM_NCHITTEST`'e `HTTRANSPARENT` döner,
  böylece SDK videosu üzerindeki tık/sürükle/bırak ana pencereye, oradan kareye gelir; Flyleaf'in
  yüzey penceresi (`Host.Surface`) ise VideoTile'da ayrıca dinlenir. Otomatik kalite eşiği yalnız
  video alanının yüksekliğine bakar (`VideoTile.VideoHeight`).

### Izgara düzenleri ve düzen seçici

- Tanım: Core [`GridLayout`](src/SPVision.Core/GridLayout.cs). Hepsi **kare** ızgara (kareler oranını korur).
  STANDART 1, 4, 9, 16, 25, 36 (N×N); BÜYÜK KARELİ 6 (3×3, 2×2 büyük (1,1)), 7 (4×4, 2×2 büyükler (1,1) (3,1) (1,3)),
  8 (4×4, 3×3 büyük (1,1)), 10 (4×4, 2×2 büyükler (1,1) (3,1)), 13 (4×4, 2×2 büyük (2,2)), 22 (5×5, 2×2 büyük (1,1)).
  Kare sırası: önce büyükler (tanım sırasıyla), sonra kalan hücreler okuma sırasıyla. Oynatma ≤16 (`PlaybackStandard/BigTile`).
- Geçiş (`ForSavedView`): kayıtlı **32 → 36** (eski 8×4 kareleri eziyordu); eski sütun ayarları aynen (1,2 → 6; 3,4 → 25);
  bilinmeyen sayı en küçük sığan düzene. Kamera atamaları sırasıyla korunur.
- Üst çubukta tek düğme: [`LayoutPicker`](src/SPVision/Controls/LayoutPicker.xaml) (36 px, en az 96; mini diyagram
  [`LayoutDiagram`](src/SPVision/Controls/LayoutDiagram.cs) + sayı + ok). Açılır panel 404 px, 6 sütun, 62 px öğeler;
  canlı ve oynatma aynı kontrolü kullanır (`SetLayouts`). Küçülen düzende dışarıda kalan kareler `Clear()` ile
  hem SDK hem Flyleaf oynatıcısını bırakır.

### Kayıt oynatma: eş zamanlı çoklu kamera

- [`PlaybackView`](src/SPVision/Views/PlaybackView.xaml.cs) kendi 260 px kenar çubuğunu taşır (takvim + "KAMERALAR n / L"
  onay listesi); canlıdaki kenar çubuğu oynatmada gizlenir. Takvimde nokta = odaklı kameranın o gün kaydı var
  (`IsapiClient.HasRecordingsAsync`: mevcut ISAPI arama isteği, gün başına 1 sonuç; ayrı uç nokta yok).
- Kamera tıklama kuralı Core [`PlaybackSlots`](src/SPVision.Core/PlaybackSync.cs): ızgarada → çıkar (kare boş kalır,
  diğerleri kesilmez); değilse ilk boş kare; ızgara doluysa odaklı karenin kamerası değişir. Düzen küçülünce taşan
  kameralar boş karelere kayar (önce odaklı kamera).
- Her kare bir [`PlaybackTile`](src/SPVision/Controls/PlaybackTile.xaml.cs) (canlı kareyle aynı görünüm): akış anahtarı
  varsa SDK, yoksa Flyleaf (FFmpeg açamazsa bir kez SDK'ya düşer). Kareler hep `TileGrid` çocuğudur; düzen değişince
  yalnız taşınır/gizlenir (yeniden açılmaz).
- **Eşitleme:** ortak saat ("master") hızla ilerler, odaklı karenin gerçek zamanını izler. Her **arama ve hız
  değişikliği tüm karelere yeniden gönderilir**. 5 sn'de bir her karenin zamanı master'la karşılaştırılır
  (`PlaybackSync.Decide`): 2 sn'den fazla (1×'in üstünde hızla orantılı) sapan kare yeniden aranır (açılış gecikmesi kadar
  önden); o saatte kaydı olmayan kare "park" edilir ("Bu saatte kayıt yok · Sonraki kayıt …") ve kaydına gelince
  kendiliğinden açılır. Kayıt cihazı bir oynatmayı reddederse (eş zamanlı oynatma sınırı vb.) sebep o karede yazar,
  diğerleri sürer; o kare bir sonraki aramada yeniden denenir.
- Atlama (`RecordingJump.Resolve`, odaklı kameranın kayıtları): kayıt yoksa sonrakine atlar + amber "Bu saatte kayıt yok,
  en yakın kayda atlandı (HH:MM:SS)"; sonrası yoksa "Bu saatten sonra kayıt yok (son kayıt HH:MM)". Hızlar 1/8×…8×
  (`PlaybackSpeed`).
- **SDK oynatma kontrol kodları** (incEn/HCNetSDK.h, `NET_DVR_PlayBackControl_V40`): `NET_DVR_PLAYSTART` 1, `PLAYPAUSE` 3,
  `PLAYRESTART` 4, `PLAYFAST` 5, `PLAYSLOW` 6 (her biri bir hız adımı), `PLAYGETTIME` 14. Yerel PlayCtrl portu aynı
  oranda tüketsin diye eşleri: `PlayM4_Fast` / `PlayM4_Slow` / `PlayM4_Pause(port, 1/0)` (plaympeg4.h). Konum değiştirme =
  `NET_DVR_PlayBackByTime_V40` ile yeniden açma (yeni tutamaç normal hızda başlar, hız adımları oradan sayılır).
  `PLAYSETTIME` (26) / `PLAYNORMAL` (7) / `SETSPEED` (24, akış bant genişliği — oynatma hızı değil) kullanılmaz.
  Hız/duraklat "istenen durum"dur (`RequestedSpeedSteps`, `RequestedPaused`) ve `SyncControls()` ile UI dışı iş
  parçacığında, karenin SDK komut kuyruğunda (`_sdkGate`) uygulanır; `GetTime` kilidi beklemez.
- Flyleaf (şifresiz RTSP) yolunda hız `Player.Speed`; RTSP kaydı cihaz hızında akar, 1×'ten hızlıda kare geride kalabilir —
  sapma düzeltmesi yeniden arar.
- **Hareket kutuları** (kayıt cihazının akışla gönderdiği yeşil hareket / akıllı analiz kutuları) PlayCtrl'ün
  "private data" çizimidir. Aç/kapa: `PlayM4_RenderPrivateData(port, tip, BOOL)` (plaympeg4.h); canlıda ek olarak
  `NET_DVR_RenderPrivateData(realHandle, tip, BOOL)` (HCNetSDK.h; RealPlay'e pencere de verildiği için SDK'nın kendi
  çizicisi de kapsansın diye). Tipler: `PLAYM4_RENDER_ANA_INTEL_DATA` (0x1) ve `PLAYM4_RENDER_MD` (0x2). Yalnızca
  görüntüleme; cihaza hiçbir şey gönderilmez. Varsayılan çizim açık olduğundan port açılırken yalnızca ayar kapalıysa
  kapatılır ([`HikRender`](src/SPVision/Hik/HikRender.cs)); ayar değişince açık portlara anında uygulanır. Flyleaf
  (şifresiz) yolunda böyle bir katman yoktur. Ayar `AppSettings.ShowMotionBoxes` (varsayılan açık); üst çubuktaki
  kesik çizgili kare düğmesi ve Ayarlar › Görüntü anahtarı aynı ayarı değiştirir.

### Temalar (açık / koyu / sistem)

- `Themes/Light.xaml` ve `Themes/Dark.xaml` **yalnızca renk + fırça** içerir ve **aynı anahtarları** taşır
  (Windows 11 paleti; kamera duvarı iki temada da koyu: `GutterBg`, `WallText`, `WallLine`).
  `Themes/Controls.xaml` yazı boyutlarını, ikonları ve tüm stilleri içerir; renklere **DynamicResource** ile bağlanır.
- [`ThemeManager`](src/SPVision/ThemeManager.cs) uygulamanın ilk birleşik sözlüğünü (tema) çalışırken değiştirir.
  Etkin tema = Core `ThemeResolver.Resolve(AppSettings.Theme, AppsUseLightTheme)` (birim testli). Sistem modunda
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` okunur ve
  `SystemEvents.UserPreferenceChanged` ile canlı izlenir; çıkışta abonelik kaldırılır. `DarkTitleBar` her geçişte tüm
  açık pencerelere `DWMWA_USE_IMMERSIVE_DARK_MODE` = 1 (koyu) / 0 (açık) uygular.
- **Kural:** görünümlerde tema fırçaları `{DynamicResource …}` ile, kodda `SetResourceReference` /
  `WithBrush(...)` ile verilir (değer olarak atanırsa tema değişince güncellenmez). Uygulama düzeyinde örtük
  `TextBlock` stili **yoktur** (şablon içindeki metinlerin rengini ezerdi); pencereler `Foreground` verir.

## 5. Klasör → dosya haritası (WPF tarafı, yollar `src/SPVision/` altına göredir)

| Dosya | Sorumluluk |
|---|---|
| `App.xaml(.cs)` | `HikSdk.Init/Shutdown`, Flyleaf `Engine.Start`, ayar yükleme; `ThemeManager.Initialize/Shutdown`, başlık çubuğunu kaydeder |
| `Themes/Light.xaml`, `Themes/Dark.xaml` | **Tema renkleri ve fırçaları** (aynı anahtarlar). Görünümlerde ham hex kullanma. (Eski oynatma takma adları `PanelBg`, `MutedFg`… kaldırıldı.) |
| `Themes/Controls.xaml` | Yazı boyutları, ikon geometrileri, kontrol stilleri (Button, `SettingsButton` 136×36, segment/sekme, `IconToggleButton`, `ToggleSwitch` 36×20, `NavButton`, `ListRowToggle`, `Card`, TextBox/arama, ListBox, ScrollBar, ToolTip, ContextMenu…) |
| `ThemeManager.cs` | Tema sözlüğünü çalışırken değiştirir; Sistem modunda Windows'u canlı izler; `ThemeChanged` olayı |
| `DarkTitleBar.cs` | DWM yerel başlık çubuğu: koyu temada 1, açıkta 0; geçişte tüm pencerelere yeniden uygulanır |
| `WindowsStartup.cs` | "Windows ile birlikte başlat": `HKCU\…\Run` altında `SPVision` = tırnaklı exe yolu |
| `Controls/GlyphIcon.cs` | 24×24 çizgi ikonları (`Icon*` geometrileri) ön plan rengiyle çizer; emoji kullanılmaz |
| `MainWindow.xaml(.cs)` | Üç görünüm (`MainSection`: Canlı İzleme, Kayıt Oynatma, Ayarlar). Üst çubuk (SPVision yazısı, sekmeler, düzen seçici (`LayoutPicker`; oynatmada ≤16 kareli ikinci seçici), kalite Oto/HD/SD, hareket kutuları, anlık görüntü/kayıt/tam ekran (F11), Ayarlar dişlisi = seçili durumlu ToggleButton), kenar çubuğu (arama, cihaz satırı, kamera listesi — "listede göster" kapalı kameralar hariç — + kare no, sürükle-bırak), durum çubuğu. Cihaz yoksa ilk açılışta Ayarlar › Cihaz yönetimi yeni satır açık halde gelir |
| `Views/SettingsView.xaml(.cs)` | **Ayarlar**: sol menü (260 px, kenar çubuğuyla aynı). *Cihaz yönetimi*: cihaz listesi (DURUM, CİHAZ ADI, ADRES, TÜR, KAMERA; durum için sayfa açılınca ISAPI deviceInfo GET), satır tıklanınca / Enter / Boşluk ile altında düzenleme paneli açılır (akordeon, bir seferde bir tane). *Uygulama ayarları*: tema, hareket kutuları, varsayılan kalite, GPU (ayarı yok → devre dışı, "Yakında"), klasörler (`OpenFolderDialog`), son görünüm, Windows ile başlat, tam ekranda aç. *Cihaz ayarları*: yalnızca "yakında" kartı — cihaza yazma yok |
| `Views/DeviceEditor.xaml(.cs)` | Satır altı düzenleme paneli (eski DeviceDialog'un yerine): Bağlantı (iki eşit sütun; şifre/anahtar kutusu kayıtlı değeri göstermez, boş bırakılırsa değişmez), "Bağlantıyı test et", "Cihazdan oku", Kameralar tablosu (LİSTEDE GÖSTER anahtarı), alt satır Cihazı kaldır · Kameraları yenile · İptal · Kaydet. Taslakla çalışır; Kaydet ayarı yazar, giriş bilgisi değiştiyse SDK oturumunu kapatır ve cihazın karelerini yeniden bağlar |
| `Controls/RoundedClip.cs` | Yuvarlak köşeli kartın içindeki satır arka planlarını köşeden taşırmadan kırpar |
| `Views/LiveView.xaml(.cs)` | 1–36 kareli ızgara (düzen tanımı: Core `GridLayout`; kayıtlı 32 → 36), sürükle-bırak/kare takası, düzeni kaydet/geri yükle; durumu `StateChanged`/`Message` ile ana pencereye bildirir |
| `Controls/VideoTile.xaml(.cs)` | Tek kamera karesi — **çift yol** (Flyleaf/SDK), başlık şeridi + video alanı (Popup yok), yeniden bağlanma (3/10/30 sn), kayıt + anlık görüntü |
| `Views/PlaybackView.xaml(.cs)` | Eş zamanlı kayıt oynatma — takvim + kamera listesi, bilgi çubuğu, ≤16 `PlaybackTile`, kontrol çubuğu (oynat/anlık görüntü/klip · 30 sn geri/ileri + "Saate git" · hız), zaman çizelgesi; eşitleme döngüsü |
| `Controls/PlaybackTile.xaml(.cs)` | Oynatma karesi: çift yol (SDK/Flyleaf), park, hız/duraklat, anlık görüntü, klip |
| `Controls/LayoutPicker.xaml(.cs)`, `LayoutDiagram.cs`, `TransientPopup.cs` | Düzen seçici, mini diyagram, geçici Popup kuralları |
| `Controls/TimelineBar.cs` | 50 px zaman çizelgesi (odaklı kamera): kutu, iz, oynatma başı; etiketler kutudan taşmaz (00:00 sola, 24:00 sağa yaslı); tık = ara, tekerlek/−/+ = yakınlaştır |
| `Hik/HikNative.cs` | Tüm P/Invoke imzaları + struct'lar (`incEn` başlıklarıyla satır satır doğrulandı; `PlayM4_RenderPrivateData` ve `NET_DVR_RenderPrivateData` dahil) |
| `Hik/HikRender.cs` | Hareket / akıllı analiz kutularını port (canlıda RealPlay tutamacı da) için aç/kapa |
| `Hik/HikSdk.cs` | Init/Cleanup, exception callback, `Available` |
| `Hik/HikSession.cs` | Cihaz başına tek login; `SdkChannel()` çevirisi |
| `Hik/HikLivePlayer.cs` | Canlı SDK oturumu (RealPlay + PlayCtrl) |
| `Hik/HikPlaybackPlayer.cs` | Oynatma SDK oturumu (PlayBackByTime + PlayCtrl + GetTime + klip SaveData + `SyncControls` hız/duraklat) |
| `Hik/HikException.cs` | SDK hata kodlarının Türkçe açıklamaları (`Describe`) |

Core tarafı: [`IsapiClient`](src/SPVision.Core/IsapiClient.cs) (Digest auth; cihaz bilgisi,
kamera listesi, kayıt arama, anlık görüntü, şifreleme durumu + doğrulama kodu okuma),
[`Models.cs`](src/SPVision.Core/Models.cs) (Device/Camera/AppSettings; `StreamKeyFor()`:
kamera anahtarı yoksa cihaz anahtarını kullanır), [`SettingsStore`](src/SPVision.Core/SettingsStore.cs)
(DPAPI şifreli JSON), [`StreamUrls`](src/SPVision.Core/StreamUrls.cs) (RTSP adresleri),
[`AppPaths`](src/SPVision.Core/AppPaths.cs) (varsayılan klasörler),
[`LegacyMigration`](src/SPVision.Core/LegacyMigration.cs) (eski addan tek seferlik ayar kopyası),
[`Theme.cs`](src/SPVision.Core/Theme.cs) (`AppTheme`, `ThemeResolver`), [`PlaybackSync.cs`](src/SPVision.Core/PlaybackSync.cs)
(`PlaybackSlots`, `RecordingJump`, `PlaybackSpeed`, `PlaybackSync`, `CalendarMonth`, `PlaybackText` — oynatmanın saf mantığı, birim testli), [`CameraList`](src/SPVision.Core/CameraList.cs)
(yenilenen kamera listesinde anahtarı ve "listede göster" seçimini korur), [`DeviceKind`](src/SPVision.Core/DeviceKind.cs)
("NVR · 32 kanal" metni) ve [`AutoStart`](src/SPVision.Core/AutoStart.cs) (Run değeri). Yeni ayarlar:
`AppSettings.Theme` (metin olarak saklanır), `ShowMotionBoxes`, `RestoreLastView`, `StartFullScreen`; `Camera.Hidden`;
`Device.DeviceType`, `Device.ChannelCount`.

## 6. Doğrulanmış gerçek durum (21.10.2026)

- Cihaz: DS-8632NI-I8, 192.168.1.2, portlar 80/554/8000, **akış şifreleme AÇIK** (dokunulmadı).
- **16 kamera aynı anda SDK yoluyla akıyor**, log temiz (`Stream Head Data Callback` 16×),
  **CPU ~%8.7** (32 çekirdeğin yüzdesi; eski iVMS 4 kamerada tek çekirdeğin %107'siydi).
- Şifresiz Flyleaf yolu daha önce test edildi: 16 kamera HD'de tek çekirdek ~%52.
- Derleme 0 hata/0 uyarı, 41/41 test (o gün 32/32 idi; geçiş testleriyle 41). Kalan tek iş: **paketleme (yayınlama)**.

## 7. Senin (Claude'un) yapacağın iş: paketleme

Hedef: çift tıklayınca çalışan, kurulum gerektirmeyen bir klasör/exe.

1. **Yayınlama komutu** (önerilen başlangıç):
   ```powershell
   dotnet publish src\SPVision\SPVision.csproj -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=false -o publish
   ```
   - `self-contained` sayede kullanıcı makinesinde .NET gerekmez (~150 MB eklenir).
     Daha küçük istenirse `--self-contained false` + framework-dependent kurulum.
   - Çıktıda olması gerekenler: `SPVision.exe`, `FFmpeg\*.dll`, `HCNetSDK.dll`,
     `PlayCtrl.dll`, `HCCore.dll`, `HCNetSDKCom\` alt klasörü (16 DLL) — csproj bunları
     otomatik kopyalar; publish sonrası **kontrol et**.
2. **Dikkat:** SDK DLL'leri lisanslıdır; ihraç edilen paketin içinde olabilirler (kullanım
   içindir) ama **repoya/internete yüklenmez**.
3. Tek dosya exe (`PublishSingleFile=true`) denenecekse: yerel DLL'ler (`HCNetSDK`,
   `PlayCtrl`) `IncludeNativeLibrariesForSelfExtract=true` gerektirir; HCNetSDKCom alt
   klasör yapısı **korunmalıdır** (SDK runtime'da `HCNetSDKCom\*.dll` arar) — bu yüzden
   **klasör dağıtımı + Inno Setup/MSIX zip tercih edilir**.
4. Yayın sonrası el ile test: boş `%APPDATA%\SPVision` ile ilk açılış (cihaz ekleme
   sihirbazı), 16 kamera canlı, oynatma, anlık görüntü, klip. Ayrıca eski
   `%APPDATA%\SP-2026 Lite\settings.json` varken ilk açılışta ayarların ve şifrelerin geldiğini
   doğrula.

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
