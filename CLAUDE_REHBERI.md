# SPVision — Claude için Tam Rehber

> **Bu belgeye ek olarak `PROJE_DURUMU.md`, `GELISTIRICI_GOREVI.md`, `README.md` ve
> `reversing/BULGULAR.md` de klasördedir.** Bu dosya hepsinin özetidir; ayrıntı için
> ilgili belgeye bak. Kodun tamamı bu klasörde çalışır durumdadır: 0 derleme hatası,
> 836/836 birim testi geçer, **gerçek cihazda (Hikvision DS-8632NI-I8) test edilmiştir.**
> (21.10.2026'daki düzen seçici + eş zamanlı çoklu oynatma değişikliği ile cihaz düzenleyicideki HTTPS varsayılanı / HTTP'ye düşme sorusu henüz cihazda denenmedi; bkz. §4, §5.)
> (Canlı izlemede sayfalar — §4 "Canlı izlemede sayfalar" — da henüz cihazda denenmedi.)
> (Ayarlar › Cihaz ayarları'ndan kayıt cihazına yazma — §9, 2. ve 3. aşama, SDK ile kamera adı ve Ağ › Portlar dahil — henüz cihazda denenmedi; okuma uç noktaları cihazda GET / SDK Get ile doğrulandı. 3. aşama (Bakım: yazılım güncelleme, fabrika ayarları) gerçek cihazda **hiç** çalıştırılmadı. İlk gerçek yazmayı sahibi uygulamadan yapar; Claude cihaza **hiçbir şey yazmaz**, deneme için bile.)
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
| `tests/SPVision.Core.Tests/` | 836 xUnit testi (`dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj`) |
| `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` | **Hikvision resmi SDK (lisanslı, repoya konmaz)** — DLL'ler `lib/`, başlıklar `incEn/`, resmi C# örnekleri `C# demo/`, C++ örnekleri `ClientDemo/`, kılavuzlar `doc/` |
| `ffmpeg/` | FFmpeg 9 paylaşımlı DLL'leri (FlyleafLib bunları kullanır) — `tools/get-ffmpeg.ps1` ile yeniden indirilebilir |
| `reversing/` | Eski iVMS'in tersine mühendislik bulguları (`BULGULAR.md` — CPU sorununun kökeni) |
| `tools/` | `get-ffmpeg.ps1` vb. yardımcı betikler |

## 3. Derleme ve çalıştırma (Windows 11, .NET 10 SDK, x64)

```powershell
cd SPVision
dotnet build SPVision.slnx          # 0 hata / 0 uyarı beklenir
dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj   # 836/836 geçer
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
  - Oynatmada SDK'ya **pencere verilmez** (`VodPara.Wnd = 0`): pencereye yalnız bizim port çizer, veri geri çağrısı akışı
    portumuza göre ayarlar ve paket atmaz (aşağıda "Kayıt oynatma" › "Tek çizici kuralı").
- **Kilit ve iş parçacığı kuralı (21.10.2026, canlıda "Seçili kamerayı kaydet" UI'yi kalıcı donduruyordu):**
  `HikLivePlayer.StartSave` oynatıcı kilidini tutarken `NET_DVR_SaveRealData_V30`'u çağırıyordu; SDK bu çağrıda veri
  callback iş parçacığını bekler, o da `OnRealData`'da her pakette aynı kilidi bekliyordu (kilit sırası kilitlenmesi).
  (1) SDK callback'leri (`OnRealData`, `OnPlayData`, `OnSdkException`) **hiçbir kilit almaz** (port `Volatile` ile
  okunur/yazılır, SYSHEAD kurulumu kilitsiz; port `Stop`'ta `StopRealPlay`/`StopPlayBack` döndükten sonra bırakılır).
  Callback'in alabileceği bir kilidi, callback iş parçacığını bekleyebilecek bir SDK/PlayCtrl çağrısı boyunca **tutma**.
  Oynatıcı kilidi yalnız kontrol çağrılarını (aç, durdur, kaydet, anlık görüntü, hareket kutuları, hız) birbirine sıralar.
  (2) Bekleyebilen SDK çağrıları (kayıt/klip başlat-durdur, `StopRealPlay`/`StopPlayBack`, Dispose, hareket kutuları, hız)
  **UI iş parçacığında çağrılmaz**: karenin `_sdkGate` kuyruğunda (`QueueSdk`; sıra UI'de alınır, komutlar verildiği
  sırayla çalışır) yürür, sonuç `Dispatcher.BeginInvoke` ile döner. Kayıt düğmesi ve KAYIT/klip rozeti karenin kendi
  bayrağına (`_hikRecording` / `_hikClipping`) bakar, hemen değişir; SDK reddederse geri alınır ve bildirilir. `IsSaving`,
  `IsRunning` kilitsiz okunur. Kapanış da artık UI'de beklemez: bkz. §8 "Kapanış".
- **Kayıt ve klip biçimi (21.10.2026 düzeltmesi, kayıt kısmı cihazda denenmedi):** Eskiden canlı kayıt
  `NET_DVR_SaveRealData_V30(…, 1, …)` ile yapılıyordu: 1 = **PS** (kılavuz: 1-PS, 2-TS, 3-RTP, 5-MP4/3GPP; koddaki "1 = MP4"
  yorumu yanlıştı) → dosya `IMKH` başlıklı Hikvision PS'iydi, içerik de **hâlâ şifreliydi** (SDK kaydederken çözmez; anahtar
  yalnız PlayCtrl'dedir). Oynatma klibi `NET_DVR_PlayBackSaveData` ile aynı akışı yazmaya çalışıyordu: **0 bayt**; üstelik
  "Klip kaydedildi" mesajı durdurma kuyrukta daha çalışmadan gösteriliyordu. PlayCtrl çözülmüş sıkıştırılmış akış vermez.
  **Şimdi** ([`HikFrameRecorder`](src/SPVision/Hik/HikFrameRecorder.cs)): kayıt/klip başlayınca **ikinci bir PlayCtrl portu**
  açılır (SYSHEAD başlığının kopyası + `PlayM4_SetSecretKey` + yazılım çözücü + `PlayM4_SetDecCBStream(1)` +
  `PlayM4_SetDecCallBackMend` + `PlayM4_Play(port, NULL)` — pencere yok); veri callback'i her paketi ekran portundan sonra
  bu porta da verir. Çözülmüş YV12 kareler Core [`VideoRecorder`](src/SPVision.Core/Recording/VideoRecorder.cs)'a gider:
  havuzdan tampona kopyala, sınırlı kuyruğa koy (dolu → kare düşer, **asla beklemez**), kendi iş parçacığında
  [`Mp4VideoWriter`](src/SPVision.Core/Recording/Mp4VideoWriter.cs) ile **H.264 / standart MP4** (FlyleafLib'in FFmpeg 9'u,
  `Flyleaf.FFmpeg.ABI` bağlamaları; B-kare yok, 2 sn'de bir anahtar kare, ~1 bit/12 piksel, 1–8 Mb/sn). Kodlayıcı sırası:
  `h264_nvenc`, `h264_qsv`, `h264_amf` (GPU), `libopenh264`, `h264_mf`, `mpeg4` (son çare, yine gerçek MP4); açılamayan
  atlanır. Zaman damgası PlayCtrl `FRAME_INFO.nStamp` (ms) → [`FrameClock`](src/SPVision.Core/Recording/FrameClock.cs):
  0'dan başlar, kesin artar; geri giden / >3 sn atlayan damga bir nominal kare ilerler (4× oynatmadaki klip de normal hızda
  oynar). Oynatmada kayıt portu ekran portuyla aynı hız adımı/duraklatmayı alır (`SyncControls`). Durdurma: port önce
  yayından çekilir (callback sayacı sıfırlanana kadar beklenir), `PlayM4_Stop` (çözücü iş parçacığını bekler; geri çağrı yalnız
  kuyruğa koyduğu için güvenli), sonra kodlayıcı kalan kareleri yazar, `moov` yazılır, dosya
  [`Mp4Inspector`](src/SPVision.Core/Recording/Mp4Inspector.cs) ile denetlenir (ftyp + dolu mdat + moov içinde video
  örnekleri; IMKH/boş/yarım dosya geçersiz). "Kayıt kaydedildi: <yol>" / "Klip kaydedildi: <yol>" **ancak bundan sonra**
  gelir; hiç kare yoksa ya da dosya geçersizse dosya silinir ve "Kayıt/Klip kaydedilemedi: <sebep>" yazar. Başlarken
  "Kayıt başladı: …" / "Klip kaydediliyor: …", durdururken "… tamamlanıyor…". Kayıt; durdur, kamera/sayfa değişimi, kalite
  değişimi, yeniden bağlanma (`Open` → `FinishRecording`), oynatmada her arama/park/kayıt sonu ve kapanışta sonlandırılır
  (oynatıcının `Stop`'u da kaydı keser değil **bitirir**; `StopSave` o bitişi devralıp raporlar). Flyleaf (şifresiz) yolunda
  Flyleaf'in remuxer'ı kalır (yeniden kodlama yok); durunca aynı denetim yapılır. Oynatma klibinde Flyleaf sesi klip boyunca
  kapatılır: Hikvision G.711 sesi MP4'e girmez, başlık yazılamaz ve 0 baytlık dosya kalırdı. H.265 kameralarda Flyleaf
  kaydı H.265 MP4 olur (VLC açar; Windows oynatıcısı HEVC uzantısı ister). Tüm bitişler `Recordings` kaydında izlenir,
  kapanış onları bekler.
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
- **Oto kalite kuralı (21.10.2026, cihazda denenmedi):** saf Core [`AutoStreamQuality.Decide`](src/SPVision.Core/AutoStreamQuality.cs)
  (birim testli). HD: büyütülmüş kare, büyük kareli düzenlerin **büyük (span > 1) kareleri** ve 1 / 4 kareli eşit ızgaraların
  tüm kareleri — boyuttan bağımsız (eskiden yalnız piksel eşiği vardı; 22 düzeninin büyük karesi 1440×900'de ~280 px video
  ile SD kalıyordu). Diğer kareler: video alanı ≥ `MainStreamMinTileHeight` (540 cihaz pikseli) ise HD; HD olan kare ancak
  %10 altına inince SD'ye düşer (histerezis, boyutlandırmada gidip gelme yok). Kayıt alan kare hiç değiştirilmez
  (`VideoTile.SetAutoQuality`; canlıdan kayıt durdurulunca yeniden değerlendirilir). `LiveView.Sync` yeni kamerayı yeni
  yerinin kalitesiyle açar (`VideoTile.Show(…, autoQuality)`; hücre yüksekliği ızgaradan tahmin edilir), böylece sayfa/düzen
  değişiminde önce SD açılıp 600 ms sonra HD'ye yeniden açılma olmaz.

### Izgara düzenleri ve düzen seçici

- Tanım: Core [`GridLayout`](src/SPVision.Core/GridLayout.cs). Hepsi **kare** ızgara (kareler oranını korur).
  STANDART 1, 4, 9, 16, 25, 36 (N×N); BÜYÜK KARELİ 6 (3×3, 2×2 büyük (1,1)), 7 (4×4, 2×2 büyükler (1,1) (3,1) (1,3)),
  8 (4×4, 3×3 büyük (1,1)), 10 (4×4, 2×2 büyükler (1,1) (3,1)), 13 (4×4, 2×2 büyük (2,2)), 22 (5×5, 2×2 büyük (1,1)).
  Kare sırası: önce büyükler (tanım sırasıyla), sonra kalan hücreler okuma sırasıyla. Oynatma ≤16 (`PlaybackStandard/BigTile`):
  **1, 4, 9, 16 / 6, 8, 10, 13** (4 + 4; 7 oynatmada yok, kayıtlı oynatma düzeni 7 → 8: `GridLayout.ForPlaybackView`).
- Geçiş (`ForSavedView`): kayıtlı **32 → 36** (eski 8×4 kareleri eziyordu); eski sütun ayarları aynen (1,2 → 6; 3,4 → 25);
  bilinmeyen sayı en küçük sığan düzene. Kamera atamaları sırasıyla korunur.
- Üst çubukta tek düğme: [`LayoutPicker`](src/SPVision/Controls/LayoutPicker.xaml) (36 px, en az 96; mini diyagram
  [`LayoutDiagram`](src/SPVision/Controls/LayoutDiagram.cs) + sayı + ok). Açılır panel: öğeler 56×62, aralık 8, kenarlardan
  14 px; sütun sayısı = uzun bölümün öğe sayısı (`SetLayouts` genişliği hesaplar): canlı 6 + 6 → 406 px, oynatma 4 + 4 →
  278 px, boş yuva yok. Panelin sağ kenarı düğmenin sağ kenarının altında (iki listede de), 4 px aşağıda;
  canlı ve oynatma aynı kontrolü kullanır (`SetLayouts`). Küçülen düzende dışarıda kalan kareler `Clear()` ile
  hem SDK hem Flyleaf oynatıcısını bırakır.

### Canlı izlemede sayfalar

- Saf mantık Core [`LivePages`](src/SPVision.Core/LivePages.cs) (birim testli). **Tek global sıralı yuva listesi**
  (`Slots`) tüm kameraları tutar; N kareli düzende sayfa p = yuvalar [p·N, (p+1)·N) (büyük kareli düzenlerde de N = kare
  sayısı, kare sırasıyla). 22 kamera: 9 → 3 sayfa, 16 → 2, 25/36 → 1. Son sayfanın boş kareleri boş durumu gösterir.
- Liste ile uzlaştırma (`Reconcile`; açılışta ve cihaz kaydet/kaldır'da): "listede göster" kapalı ya da silinen kameralar
  sıradan çıkar (arkadakiler kayar), eksik listelenen kameralar önce boşlukları, sonra sonu doldurur; sonda boşluk kalmaz.
  "Açılışta son görünümü geri yükle" kapalıysa varsayılan düzen + liste sırası + 1. sayfa.
- Sürükle-bırak: kenar çubuğundan bir kareye bırakılan kamera başka bir yuvadaysa **yer değiştirir** (kopya oluşmaz);
  yuvası yoksa karedeki kamera ilk boşluğa (yoksa sona) gider. Kare → kare = iki yuvanın takası (boş kareye taşıma dahil).
  "Kareyi kapat" yuvayı boşaltır (kamera bir sonraki uzlaştırmada geri gelir). Hepsi global sırada olduğu için sayfa ve
  düzen değişince korunur. Kenar çubuğunda çift tık: kamera bir sayfadaysa o sayfaya gidilir ve karesi seçilir.
  Cihaz satırına çift tık: o cihazın çevrim içi kameraları başa, 1. sayfa (düzen artık büyütülmez).
- Durum çubuğu üç sütunlu (`*` / `Auto` / `*`): ortada `‹ Sayfa 1 / 3 ›` (23×23 `StatusIconButton`, metin sabit 88 px),
  pencerenin tam ortasında; tek sayfada ve canlı dışında gizli. **Ayarlar'da** solda yalnız bağlantı ("Bağlı · cihaz", arkada
  çalışan canlı duvara göre), sağda CPU + saat (+ geçici mesaj) görünür; "n / L kamera ekranda", yeniden bağlanma ve video
  yolu ("Şifreli akış · SDK · GPU çözme") gizlenir. Oklar ve PageUp/PageDown (yalnız canlıda, metin kutusu
  odaktayken değil, `PreviewKeyDown`) başa/sona sarar. Büyütülmüş kare varsa önce küçültülür.
- Düzen değişince sayfanın ilk kamerası görünür kalır (onu içeren sayfa açılır). Sayfa ve sıra `LastView`'a
  (`Tiles` = global sıra, `Page`) kaydedilir; açılışta sınırlanır. Eski kayıtlarda `Tiles` tek düzenin kareleriydi = sıranın başı.
- **Akışlar:** `LiveView.Sync` kareleri `LivePages.AssignTiles` ile eşler: istenen kamerayı zaten gösteren kare yalnız
  yeni hücresine **taşınır** (yeniden bağlanmaz — takas, düzen değişimi, gizlenen kameradan sonra kayma); yalnız kamerası
  değişen kareler yeniden açılır ("Bağlanıyor"). Kareler ağaçtan çıkarılmaz (§8). Kamerası değişen karede süren MP4 kaydı durur.

### Kayıt oynatma: eş zamanlı çoklu kamera

- [`PlaybackView`](src/SPVision/Views/PlaybackView.xaml.cs) kendi 260 px kenar çubuğunu taşır (takvim + "KAMERALAR n / L"
  onay listesi); canlıdaki kenar çubuğu oynatmada gizlenir. Takvimde nokta = odaklı kameranın o gün kaydı var
  (`IsapiClient.HasRecordingsAsync`: mevcut ISAPI arama isteği, gün başına 1 sonuç; ayrı uç nokta yok).
- **Son oynatma durumu (21.10.2026, cihazda denenmedi):** düzen + işaretli kameralar (kare sırasıyla, boşluksuz)
  `AppSettings.LastPlaybackView`'a yazılır (düzen seçimi ve kamera tıklamasında). Açılışta "Açılışta son görünümü geri yükle"
  açıksa Core [`PlaybackRestore.From`](src/SPVision.Core/PlaybackRestore.cs) (birim testli): kayıtlı 7 → 8, silinen/gizlenen
  kameralar ve kopyalar atılır, düzenin kare sayısına kırpılır. Düzen hemen gelir; kameralar Kayıt Oynatma **ilk açıldığında**
  karelere konur (açılışta cihaza arama gitmez), günün kayıtları aranır ama **oynatma başlamaz** (`_awaitUser`): kare "Oynatmaya
  hazır" der, saat `--:--:--` kalır. Kullanıcı gün seçince (gösterilen güne tıklamak dahil), Oynat'a basınca, aradığında
  (zaman çizelgesi, ±30 sn, "Saate git") ya da **yeni kamera eklediğinde** eski davranış (ilk kayıttan oynat) geri gelir;
  düzen değiştirmek, kare odaklamak, kamera çıkarmak başlatmaz. Gün ve saat saklanmaz.
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
- **Oynatma zamanı kuralı (21.10.2026, gece yarısı hatası; cihazda okuma sondasıyla doğrulandı):** Kayıt cihazı
  `PlayBackByTime_V40`'ı bazen istenen saatten değil **kayıt dosyasının başından** başlatır: 21 Ekim 00:00 istenince DIS KAPI
  20 Ekim 23:11:04'ten açıldı (o kayıt parçası 23:10:49'da başlıyor; KARŞILAMA'da da 23:21). `NET_DVR_PLAYGETTIME` bunu
  göstermez (SDK'nın istenen başlangıçtan tahmini; pencere verilmezse hata 12 döner), bu yüzden sapma düzeltmesi hiç
  görmedi. `NET_DVR_PLAYSETTIME` (26) çalışan tutamaçta görünür bir şey değiştirmedi. **Kural:** SDK karesinin zamanı
  **daima** kendi portumuzdan `PlayM4_GetSystemTime` (akıştaki mutlak zaman = kameranın OSD'si, ms'li, yerel çağrı) ile
  okunur; `PLAYGETTIME` artık hiç kullanılmaz (SDK'ya pencere verilmiyor, bkz. "Tek çizici kuralı"). İlk karenin zamanı bilinince kare
  [`PlaybackStart.IsWrong`](src/SPVision.Core/PlaybackSync.cs) ile denetlenir: istenenden 30 sn'den erken ise yanlış başlangıçtır,
  kare hemen (ortak saatin ilerlediği kadar ileriye) yeniden açılır ("Konum düzeltiliyor…", en çok 2 kez). Doğru bir açılış da
  istenen saatten **önceki anahtar kareden** başlar (sondada 00:00:00 için 23:59:56,19 = 3,8 sn önce; 12:30:00 için 12:29:57,75;
  bu kamerada GOP ~6,7 sn).
- **"İstenen saatten öncesi asla görünmez" kuralı (21.10.2026, cihazda okuma sondasıyla ölçüldü; uygulamada elle denenmedi):**
  Eski hata: takvimde güne çift tıklayınca kare önce **önceki günün 23:59:56'sını** (kameranın OSD'si, görüntüye yakılı) ~4 sn
  normal hızda oynatıyordu; ortak saat/çizelge `ClampDay` sayesinde 00:00'ın altına inmiyordu ama 12:30 gibi gün içi aramalarda
  odak karesinin zamanını izleyerek 12:29:57'yi de gösterebiliyordu. Dosya başından açılışta (23:11) yanlış görüntü yeniden
  açılana kadar görünüyordu. **Şimdi (SDK yolu):** her açılışta (`SeekCore` → `OpenHik`: takvim, çizelge, ±30 sn, "Saate git",
  sapma düzeltmesi, Durdur sonrası Oynat) `HikVideoHost` **`Visibility.Hidden`** olur (HWND ve boyutu kalır, PlayCtrl çizmeye
  devam eder, kullanıcı görmez; başlıkta "Bağlanıyor…"). `Started` → karenin `_sdkGate` kuyruğunda `HikPlaybackPlayer.CatchUp`:
  5 ms'de bir yerel `PlayM4_GetSystemTime` (bizim port = pencerenin tek çizicisi), karar saf Core [`PlaybackCatchUp.Decide`](src/SPVision.Core/PlaybackSync.cs) (birim testli): istenen saatten 1 sn'den
  (`SlowLead`) fazla gerideyse **4×** (`Steps` = 2: kayıt cihazına 2 × `PLAYFAST` + portumuza 2 × `PlayM4_Fast`; **8× ve üstünde
  kayıt cihazı yalnız anahtar kare gönderir** — sondada 23:59:56 → 00:00:02,9 → 00:00:09,6 atladı — bu yüzden 4×), son saniye
  normal hızda (yavaşlatma ~250 ms geç işler: 4×'te 1,1 sn kayıt), **portumuz istenen saat + 120 ms'yi** (`Margin`) geçince
  paylaşılan hız/duraklat uygulanır (duraklatılmışsa duraklatma burada), pencere gösterilir (`OnCatchUpDone`; ardından
  `PlayM4_RefreshPlay` — duraklatılmış görüntü yeniden gösterilen pencerede siyah kalmasın), kare `Playing` olur ve ancak o
  zaman `CurrentTime` verir. Ölçüm: 3,8 sn geride → ~1,0–1,3 sn gizli, 2,2 sn → ~0,9 sn; görünen ilk kare T+0,5–0,6 sn, sonra
  düz 1×. 4 sn içinde zaman gelmezse (`TimeWait`) ya da 10 sn'de (`Limit`) yetişemezse yine gösterilir. Yakalama sürerken
  anlık görüntü ve **klip başlatılamaz** (`IsCaughtUp`/`_timeVerified`; `HikFrameRecorder` istenen saatten önceki kareyi
  kaydetmez), dürtme yapılmaz (`CanNudge`), kullanıcının hız/duraklat komutları kuyrukta yakalamadan sonra uygulanır; yeni arama /
  park / durdurma / kapanış yakalamayı nesil numarasıyla keser. `UsesSdk` artık `HikHost.Visibility != Collapsed`'dır (gizli de
  SDK yoludur). Ortak saat: `PlaybackView._seekFloor` = son istenen saat; saat odaklı kareyi yalnız
  `PlaybackCatchUp.MayFollow` (≥ taban) ise izler, böylece saat/çizelge/"Saate git" istenen saatten öncesini hiç göstermez.
  **Flyleaf (şifresiz RTSP) yolunda bu yapılamaz:** FFmpeg karelerin mutlak zamanını vermez (zaman `_openedAt + CurTime`
  tahminidir), kayıt cihazının anahtar kareden başlattığı birkaç saniye OSD'de görünebilir; saat yine tabanın altına inmez.
- **Tek çizici kuralı (21.10.2026, oynatmada OSD saniyesi ileri-geri atlıyor / görüntü takılıyordu; 4 kanallı okuma
  sondasıyla doğrulandı, uygulamada elle denenmedi):** Kullanıcı: 4'lü oynatmada normal hızda **dört karenin hepsinde aynı
  anda** OSD saati (ss:dd:sn) ileri-geri atlıyor, görüntü takılıyordu. Sebep: `NET_DVR_PlayBackByTime_V40`'a pencere
  (`VodPara.Wnd`) veriliyordu; SDK oynatmayı **kendi PlayCtrl portuyla da** aynı pencereye çözüp çiziyordu (anahtarı da
  çözüyordu), bizim port da çiziyordu: tek pencerede iki çizici, kareler sırayla birinden ve ötekinden geliyordu. Her hız
  değişiminden sonra (`PLAYSLOW` + `PlayM4_Slow`) ikisi ayrışır: sondada her aç-yakala (4× → 1×) sonrası 4 kanaldan 3'ünde
  bizim port SDK portundan **0,25–0,53 sn ileride**, örneklerin %99'unda > 80 ms (2+ kare) fark — yani her açılışta, tüm
  karelerde aynı anda; her hızlandır-yavaşlat (dürtme, kullanıcı) ~+0,3 sn ekler; 8× → 1× sonrası fark **9–30 sn**. Dürtme
  tek başına salınmıyordu (başlama 2 sn / bitiş 0,5 sn eşiği vardı) ama her dürtme bu ayrışmayı büyütüyordu. **Şimdi:**
  SDK'ya **pencere verilmez** (`VodPara.Wnd = 0`; SDK portu açılmaz: `GetPlayBackPlayerIndex` = −1, ikinci GPU çözümü de
  gider), pencereye yalnız bizim port çizer. Pencere olmayınca SDK akışı hızlandırmaz/yavaşlatmaz: kayıt cihazı ağın
  izin verdiği hızda gönderir (sondada 45 sn'de 156 yerine 727 paket) ve eski "100 ms dene, sonra paketi at" girişi
  **paket düşürüyordu** (158 düşen paket = bulanık görüntü). Bu yüzden veri geri çağrısı akışı bizim porta göre ayarlar
  (`HikPlaybackPlayer.QueueAhead` = 3 MB): portta 3 MB'tan fazla oynatılmamış veri varken bekler
  (`PlayM4_GetSourceBufferRemain`), sonra `PlayM4_InputData` kabul edene kadar dener — **paket asla atılmaz**; `Stop`
  önce `_playHandle = -1` yapar, bekleyen geri çağrı çıkar, sonra `NET_DVR_StopPlayBack`. Duraklatmada geri çağrı duraklatma
  boyunca bekler (sondada 60 sn ve 300 sn duraklatma: istisna yok, devamda temiz). Ölçüm (4 kanal, sonda): 1,00×, en uzun
  donma 1 kare (~42 ms), geri giden zaman 0, düşen paket 0; 4× yakalama, duraklat/devam, dürtmeler ve 8× normal çalışır
  (`PLAYFAST/SLOW/PAUSE/RESTART` pencere olmadan da "OK"). Düzeltilmiş `HikPlaybackPlayer` ile 4 karelik sonda: yakalama
  sonrası ilk görünen kare T+0,13–0,19 sn (eskiden T+0,5–0,6), istenen saatten önceki zaman 0 kez, dürtmeler sonrası 1,00×,
  duraklatılmış 4 oynatıcının `Stop`'u 54 ms, oynatırken `Dispose` 30 ms; `PlayBackByTime_V40` 2,4 sn yerine 0,5 sn'de döner
  (ilk kare zamanı ~2,2 sn'de, eskiden 2,5–2,8 sn). Hareket kutuları, anlık görüntü (`PlayM4_GetJPEG`),
  `RefreshPlay`, zaman (`PlayM4_GetSystemTime`) ve klip portu (`HikFrameRecorder`, aynı paketler) zaten bizim porttaydı.
  `PLAYGETTIME` yedeği kaldırıldı (pencere yokken hata 12). Kayıt sonu sinyali iki modda da aynı (sondada ne veri tipi 12 ne
  istisna geldi; kayıt sonu yine sapma döngüsünün park kuralıyla yakalanır). Bilinen durum (eskiden de vardı, iki modda da
  ölçüldü): 8×'ten normal hıza dönünce kayıt cihazının 8×'te yalnız anahtar kare gönderdiği tamponlu kısım birkaç saniye hızlı
  akar. Canlı izlemede (`RealPlay`'e de pencere verilir) iki çizici durumu sürer ama orada hız değişimi olmadığı için ayrışma
  ölçülmedi; gerekirse aynı düzeltme uygulanabilir.
- Sapma: SDK karelerinde [`PlaybackSync.Decide`](src/SPVision.Core/PlaybackSync.cs) (dürtme aşırı yüklemesi) birkaç saniyelik
  farkı **yeniden açmadan** düzeltir: geride → bir hız adımı hızlı (`CatchUp`), önde → duraklat (`Hold`), 0,5 sn içine
  gelince eski hız (`Resume`); dürtülen kare her tik (250 ms), dürtülmeyen 5 sn'de bir denetlenir. Histerezis: dürtme 2 sn'den
  (1× üstünde hızla orantılı) fazla farkta başlar, 0,5 sn içinde biter; ayrıca bir karenin dürtmesi **1 sn'den sık değişmez**
  (`MinNudgeHold`; `PlaybackTile.SinceNudgeChange` = son başlama/bitişten beri geçen süre — hız değişimi ~250 ms geç işlese
  de aç-kapa salınımı olmaz; birim testli, 250 ms'lik tik benzetimi: 3 sn geride kare tam bir `CatchUp` + bir `Resume` alır).
  20 sn'den (1× üstünde hızla orantılı) fazla fark ya da 25 sn'de sonuç vermeyen dürtme yeniden açılır (1 sn kuralından önce).
  Odaklı kare (referans) ve Flyleaf kareleri dürtülmez (eski kural: 2 sn → yeniden aç).
  Kullanıcı hız/duraklat/arama yapınca dürtme biter. Yakalama süren (gizli, `Connecting`) kare denetlenmez.
- **Durdur düğmesi (21.10.2026, cihazda denenmedi):** kontrol çubuğunda Oynat'ın hemen sağında, aynı stil
  (`PlayerAccentButton` 30×30, 4 px aralık), simge `IconStop` (Segoe Fluent Icons E71A'nın şekli: 11×11 yuvarlak köşeli dolu
  kare, uygulamanın simgeleri gibi geometri), ipucu/ad "Durdur", AutomationId `PlaybackStop`. Sol grup 132 px, sağ grup 98 px;
  yan sütunlar eşit `*` olduğu için orta grup tam ortada kalır. Kural saf Core [`PlaybackTransport`](src/SPVision.Core/PlaybackSync.cs)
  (birim testli; `NoTime / Playing / Paused / Stopped`): Durdur yalnız oynarken/duraklatılmışken ve karede kamera varken etkin;
  basınca saat durur (`_stopped`, `Master` = `_anchor`, çizelge imleci yerinde), her kare `PlaybackTile.Stop()`: klip bitirilir
  ("Klip kaydedildi/kaydedilemedi" yine gelir), akış karenin `_sdkGate` kuyruğunda kapanır (UI beklemez), kamera kalır, kare
  "Durduruldu · Oynat'a basın" der; bilgi çubuğunda "Durduruldu", Oynat simgesi. Oynat (`Restart`) = tüm kareler saklanan saate
  `StartAt` (yukarıdaki gizli yakalama kuralıyla). Durdurulmuşken kareler kendiliğinden açılmaz (`TilesJoin`: eklenen kamera,
  biten arama, görünüme dönüş, sapma döngüsü); arama (çizelge, ±30 sn, "Saate git") ve gün seçimi/çift tık oynatmayı yeniden başlatır.
- **Hareket kutuları** (kayıt cihazının akışla gönderdiği yeşil hareket / akıllı analiz kutuları) PlayCtrl'ün
  "private data" çizimidir. Aç/kapa: `PlayM4_RenderPrivateData(port, tip, BOOL)` (plaympeg4.h); canlıda ek olarak
  `NET_DVR_RenderPrivateData(realHandle, tip, BOOL)` (HCNetSDK.h; RealPlay'e pencere de verildiği için SDK'nın kendi
  çizicisi de kapsansın diye). Tipler: `PLAYM4_RENDER_ANA_INTEL_DATA` (0x1) ve `PLAYM4_RENDER_MD` (0x2). Yalnızca
  görüntüleme; cihaza hiçbir şey gönderilmez. Varsayılan çizim açık olduğundan port açılırken yalnızca ayar kapalıysa
  kapatılır ([`HikRender`](src/SPVision/Hik/HikRender.cs)); ayar değişince açık portlara anında uygulanır. Flyleaf
  (şifresiz) yolunda böyle bir katman yoktur. Ayar `AppSettings.ShowMotionBoxes` (varsayılan açık); üst çubuktaki
  kesik çizgili kare düğmesi ve Ayarlar › Görüntü anahtarı aynı ayarı değiştirir.
- **Oynatmada hareket kutuları:** eskiden `NET_DVR_PlayBackByTime_V40`'a pencere verildiği için SDK'nın kendi portu da
  çiziyordu ve kutular orada kapanmıyordu (16e8878 o portu `NET_DVR_GetPlayBackPlayerIndex` ile bulup kapatıyordu).
  "Tek çizici kuralı"ndan beri SDK'ya pencere verilmez, SDK portu yoktur: anahtar yalnız bizim porta uygulanır — port
  açılırken (SYSHEAD, ayar kapalıysa) ve ayar değişince (`SetMotionBoxes`, karenin `_sdkGate` kuyruğunda, UI dışı).
  `HikRender.SetPlaybackMotionBoxes` ve `GetPlayBackPlayerIndex` bildirimi kaldırıldı. Her arama yeni
  tutamaç + yeni port = varsayılan (açık), bu yüzden her açılışta yeniden uygulanır. Flyleaf yolu FFmpeg ile yalnız video
  akışını çözer; Hikvision özel veri paketlerini çizmez (kutular orada hiç görünmez). Kutular kameranın kendisinde
  görüntüye "yakılmışsa" (kamera OSD ayarı) hiçbir istemci kaldıramaz.
- **Takvim noktaları ve zaman çizelgesi (21.10.2026):** nokta = **işaretli kameralardan herhangi birinin** o gün kaydı
  var (yalnız odaklı kamera değil); gelecek günlere nokta konmaz; seçili günde nokta beyazdır (`SelectedFg`), diğerlerinde
  yeşil (`Recorded`: açık `#0F7B0F`, koyu `#6CCB5F` — "online/ok" yeşiliyle aynı). Saf mantık Core
  [`RecordedDays`](src/SPVision.Core/PlaybackSync.cs) (kamera + gün başına cevap; birleşim; başka kameranın noktaladığı gün
  tekrar sorulmaz; en yeni günden geriye). Kamera başına arka planda bir arama döngüsü (`_markLoaders`, en çok 2 eş zamanlı
  istek), cevaplar geldikçe noktalar tek tek belirir; kamera çıkarılınca / ay değişince döngüsü durur. (Eski hata: tek
  odaklı kamera, 31 istek bitmeden hiç nokta yok, her odak/gün değişiminde baştan başlıyor, tek bir hata tüm ayı siliyordu.)
  Oynatma gününün tam araması da o günün cevabı olarak yazılır. Zaman çizelgesi: odaklı kameranın kayıtları güne kırpılıp
  birleştirilerek (`RecordingJump.ClipToDay`) yeşil çubuk olarak çizilir; odak başka kameraya geçince o kameranın kayıtları
  arka planda yeniden aranır (bilinenler, cihaz cevap vermezse korunur). Arama başarısız olursa bilgi çubuğunda amber
  "Kayıt listesi alınamadı: …" yazar (eskiden sessizdi: yeşil yoktu, oynatma gece yarısından başlıyordu).
  `SearchRecordingsAsync` sonraki sayfayı **gelen sonuç sayısı kadar** ilerletir (cihaz istenenden az döndürebilir; eskiden
  100 atlanıyordu) ve okunamayan zaman damgalı tek öğe tüm aramayı düşürmez.
- **Takvimde çift tık (21.10.2026, cihazda denenmedi):** tek tık günü seçer (değişmedi). Bugün/geçmiş güne çift tık
  ya da odaktaki gün düğmesinde **Enter** = günü seç + tüm kareleri **00:00'a** ara, geçerli hızda oynat (duraklatılmışsa
  oynar). Ayrım: tık 1 (bırakınca `Click` → `SelectDay`), 2. basışta `MouseDoubleClick` → `PlayDay`, 2. bırakışta gelen
  `Click` aynı gün olduğu için `SelectDay`'de etkisizdir (ek arama/arama yok). Enter `PreviewKeyDown`'da yakalanır (düğmenin
  kendi Enter tıklaması çalışmaz). Gün aramaları bitmemişse `_startFromMidnight` bayrağı `TryStart`'a "00:00'dan başla"
  der. Karar Core [`DayStart.Resolve`](src/SPVision.Core/PlaybackSync.cs): odaklı kamera 00:00'da kayıtlıysa (gece yarısını
  aşan kayıt dahil) 00:00; değilse günün ilk kaydı + amber "00:00'da kayıt yok · ilk kayıt HH:MM:SS'den başlatıldı";
  odaklıda hiç yoksa (tüm aramalar bitince) diğer kameraların en erkeni; kimsede yoksa kareler park + "Bu günde kayıt yok";
  arama başarısızsa (bilinmiyor) 00:00. Kamera yoksa "Önce kamera seçin"; gelecek günler devre dışı. İpucu: "Çift tıklayın:
  günün kaydını 00:00'dan oynat".
- **Yeşil noktalar görünmüyordu (21.10.2026 düzeltmesi, cihazda doğrulanmadı):** nokta yükleyicisi ilk hatalı gün
  aramasında (kayıt cihazı oynatma aramalarıyla meşgul, 10 sn zaman aşımı…) **sessizce duruyordu**; kalan günler hiç
  sorulmuyordu. HTTP 200 ile gelen bir ret (`ResponseStatus` belgesi ya da `responseStatus` false) ise "kayıt yok" diye
  okunup o gün **kalıcı olarak** noktasız kalıyordu. Şimdi: arama reddi `IsapiException` atar ("NO MATCHES" yine "kayıt
  yok"dur); hata hiçbir zaman "kayıt yok" diye saklanmaz, aynı gün 5 / 15 / 30 sn sonra yeniden sorulur
  (`RecordedDays.RetryDelay`), üçüncü hatadan sonra durum çubuğunda "Takvim için kayıt günleri alınamadı (kamera): …"
  yazar; gün araması zaman aşımı 20 sn (oynatma aramasıyla aynı), aynı anda 1 gün araması.
- **Takvim noktaları ay sorgusuyla (21.10.2026, cihazda doğrulandı):** kayıt cihazının kendi web arayüzünün takvim
  sorgusu kullanılır: `POST ISAPI/ContentMgmt/record/tracks/{kanal×100+1}/dailyDistribution`, gövde
  `<trackDailyParam><year>…</year><monthOfYear>…</monthOfYear></trackDailyParam>` (ad alanısız, arama gövdesi gibi),
  cevap `trackDailyDistribution/dayList/day` (`dayOfMonth`, yoksa `id`; `record` true/false; ad alanı olsa da olmasa da
  okunur) → `IsapiClient.GetRecordedDaysAsync`. **Salt okunur** (arama gibi POST; `IsapiWritePolicy`/PUT yolundan geçmez,
  izinli PUT listesi bu yolu kapsamaz — testli). İşaretli kamera başına ay başına **1 istek** (en çok 2 eş zamanlı),
  `RecordedDays.SetMonth` ile birleşir (bugünden sonrası saklanmaz → nokta yok). 400/403/404/405/501 ya da gün listesi
  olmayan / okunamayan cevap → o kamera için eski gün gün aramaya düşülür; diğer hatalar 5/15/30 sn sonra yeniden.
  DS-8632NI-I8'de 200, ~0,1–0,5 sn. (Yukarıdaki "ayrı uç nokta yok" artık geçersiz.) Eski gün gün döngüsünün
  kusurları da düzeltildi: sorulacak gün kapıdan (`_markSearches`) **önce** seçildiği için her işaretli kamera her
  günü ayrı ayrı soruyordu (8 kamerada 21 yerine 168 istek, sırayla); `IsapiException` dışı hata (gövde okunurken kopan
  bağlantı) döngüyü sessizce bitiriyordu; cihazın bağlantı ayarı değişince (`ReloadDevice`, ör. HTTP → HTTPS) döngü
  eski adresle sürüyordu — artık o cihazın döngüleri yeniden başlatılır.

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
| `Themes/Controls.xaml` | Yazı boyutları, ikon geometrileri, kontrol stilleri (Button, `SettingsButton`/`SettingsAccentButton`/`SettingsDangerButton` 120×30 · köşe 4 · yazı 12 — Ayarlar'daki tüm düğmeler ve onay pencereleri, segment/sekme, `IconToggleButton`, `ToggleSwitch` 36×20, `NavButton`, `ListRowToggle`, `Card`, TextBox/arama, ListBox, ScrollBar, ToolTip, ContextMenu…) |
| `ThemeManager.cs` | Tema sözlüğünü çalışırken değiştirir; Sistem modunda Windows'u canlı izler; `ThemeChanged` olayı |
| `DarkTitleBar.cs` | DWM yerel başlık çubuğu: koyu temada 1, açıkta 0; geçişte tüm pencerelere yeniden uygulanır |
| `WindowsStartup.cs` | "Windows ile birlikte başlat": `HKCU\…\Run` altında `SPVision` = tırnaklı exe yolu |
| `Controls/GlyphIcon.cs` | 24×24 çizgi ikonları (`Icon*` geometrileri) ön plan rengiyle çizer; emoji kullanılmaz |
| `MainWindow.xaml(.cs)` | Üç görünüm (`MainSection`: Canlı İzleme, Kayıt Oynatma, Ayarlar). Üst çubuk (SPVision yazısı, sekmeler, düzen seçici (`LayoutPicker`; oynatmada ≤16 kareli ikinci seçici), kalite Oto/HD/SD, hareket kutuları, anlık görüntü/kayıt/tam ekran (F11), Ayarlar dişlisi = seçili durumlu ToggleButton), kenar çubuğu (arama, cihaz satırı, kamera listesi — "listede göster" kapalı kameralar hariç — + kare no, sürükle-bırak), durum çubuğu. Cihaz yoksa ilk açılışta Ayarlar › Cihaz yönetimi yeni satır açık halde gelir |
| `Views/SettingsView.xaml(.cs)` | **Ayarlar**: sol menü (260 px, kenar çubuğuyla aynı). *Cihaz yönetimi*: cihaz listesi (DURUM, CİHAZ ADI, ADRES, TÜR, KAMERA; durum için sayfa açılınca ISAPI deviceInfo GET), satır tıklanınca / Enter / Boşluk ile altında düzenleme paneli açılır (akordeon, bir seferde bir tane). *Uygulama ayarları*: tema, hareket kutuları, varsayılan kalite, GPU (ayarı yok → devre dışı, "Yakında"), klasörler (`OpenFolderDialog`), son görünüm, Windows ile başlat, tam ekranda aç. *Cihaz ayarları*: [`RecorderSettingsView`](src/SPVision/Views/RecorderSettingsView.xaml.cs) (§9) |
| `Views/RecorderSettingsView.xaml(.cs)`, `Themes/RecorderControls.xaml` | **Cihaz ayarları** sayfası (§9): cihaz seçici + "Web arayüzünü aç" (180×32), 9 eşit sekme; kartlar (başlık + açıklama · Geri al / Cihaza uygula), satırlar (etiket + yardım · 240 px denetim). Sayfanın kendi stilleri (temalı ComboBox, `SettingsToggleButton`, sekme şeridi, rozet) `RecorderControls.xaml`'da, yalnız bu sayfada birleştirilir. Rozet zeminleri `OnlineSoftBg` / `WarningSoftBg` (Light/Dark) |
| `Views/ConfirmDialog.cs` | Adlandırılmış düğmeli tema uyumlu soru; liste aşırı yüklemesi değişiklikleri satır satır (başlıklı bölümler: UYGULANDI / UYGULANMADI) kaydırmalı kutuda gösterir; `cancel` null = tek düğme |
| `Views/RecorderSettingsView.{Events,Network,Users,Risky}.cs` | Cihaz ayarları 2. aşama (§9 "2. aşama"): Olaylar, Ağ (adres + portlar), Kullanıcılar sekmeleri; riskli işlemler (`RunRiskyAsync`, yeniden başlat, disk biçimlendir); `DeviceUpdated` olayı (adres / şifre değişince SettingsView kaydeder + yeniden bağlar) |
| `Views/RiskyConfirmDialog.cs` | Riskli işlemin **ayrı onayı** (`IRiskyConfirmation`'ın tek uygulaması): başlık, amber uyarı bandı (`Severe` istekte kırmızı: fabrika ayarları), eski → yeni satırları, yazılacak kelime kutusu (`BİÇİMLENDİR`, `GÜNCELLE`, `SIFIRLA`); odak Vazgeç'te, Enter onaylamaz. `DialogChrome` (tema + sayfa stilleri + uyarı/tehlike bandı) burada |
| `Views/RecorderSettingsView.Maintenance.cs` | Cihaz ayarları 3. aşama, **Bakım** sekmesi (§9 "3. aşama"): kayıt cihazı bilgisi + "Web arayüzünü aç", kayıt cihazı yazılımı (Denetle / Dosyadan yükle… / Güncelle…), kamera yazılımları (onay kutulu tablo, sırayla güncelleme), fabrika ayarları (Temel / Tam sıfırla…). `FirmwareBusy` sürerken sekmeler, cihaz seçici ve pencere kapatma kilitlenir (kapatmada soru) |
| `Hik/HikFirmware.cs` | Yalnız yazılım güncellemesinin SDK çağrıları (`NET_DVR_Upgrade_V50` + `ENUM_UPGRADE_DVR`, `NET_DVR_StartRemoteConfig` + `NET_DVR_UPGRADE_IPC`); `HikNative`'den bilerek ayrı. Yalnız onaydan çıkan `FirmwareJob` kabul eder |
| `Views/UserFormDialog.cs` | Kullanıcı ekle / şifre değiştir formu: ad, yetki (Operatör/İzleyici), şifre ×2, SPVision hesabının şifresi (onay; kayıtlı şifreyle karşılaştırılır, yanlışsa cihaza gitmez) |
| `Views/DeviceEditor.xaml(.cs)` | Satır altı düzenleme paneli (eski DeviceDialog'un yerine): Bağlantı (iki eşit sütun; şifre/anahtar kutusu kayıtlı değeri göstermez, boş bırakılırsa değişmez), "Bağlantıyı test et", "Cihazdan oku", **HTTPS kullan** (ipucu: ayar/bilgi trafiğini şifreler, görüntü akışını etkilemez; yeni cihaz HTTPS + 443 ile açılır, kayıtlı cihaz kendi değerleriyle; işaretleyince 80→443, kaldırınca 443→80, özel port değişmez — Core `HttpsPort.Follow`; HTTPS açıkken cihaza TCP/TLS düzeyinde hiç ulaşılamazsa (`IsapiException.ConnectionFailed`, 401 vb. yanıtlar hariç) "HTTP ile dene / İptal" sorulur ([`ConfirmDialog`](src/SPVision/Views/ConfirmDialog.cs)), kabul edilirse forma HTTP (443 ise 80) yazılır ve bir kez yeniden denenir — sessiz düşürme yok), Kameralar tablosu (LİSTEDE GÖSTER anahtarı), alt satır Cihazı kaldır · Kameraları yenile · İptal · Kaydet. Taslakla çalışır; Kaydet ayarı yazar, giriş bilgisi değiştiyse SDK oturumunu kapatır ve cihazın karelerini yeniden bağlar |
| `Controls/RoundedClip.cs` | Yuvarlak köşeli kartın içindeki satır arka planlarını köşeden taşırmadan kırpar |
| `Views/LiveView.xaml(.cs)` | 1–36 kareli ızgara (düzen tanımı: Core `GridLayout`; kayıtlı 32 → 36), sayfalar (Core `LivePages`, `Sync`), sürükle-bırak/kare takası, düzeni + sayfayı kaydet/geri yükle; durumu `StateChanged`/`Message` ile ana pencereye bildirir |
| `Controls/VideoTile.xaml(.cs)` | Tek kamera karesi — **çift yol** (Flyleaf/SDK), başlık şeridi + video alanı (Popup yok), yeniden bağlanma (3/10/30 sn), kayıt + anlık görüntü |
| `Views/PlaybackView.xaml(.cs)` | Eş zamanlı kayıt oynatma — takvim + kamera listesi, bilgi çubuğu, ≤16 `PlaybackTile`, kontrol çubuğu (oynat/durdur/anlık görüntü/klip · 30 sn geri/ileri + "Saate git" · hız), zaman çizelgesi; eşitleme döngüsü |
| `Controls/PlaybackTile.xaml(.cs)` | Oynatma karesi: çift yol (SDK/Flyleaf), park, hız/duraklat, anlık görüntü, klip |
| `Controls/LayoutPicker.xaml(.cs)`, `LayoutDiagram.cs`, `TransientPopup.cs` | Düzen seçici, mini diyagram, geçici Popup kuralları |
| `Controls/TimelineBar.cs` | 50 px zaman çizelgesi (odaklı kamera): kutu, iz, oynatma başı; etiketler kutudan taşmaz (00:00 sola, 24:00 sağa yaslı); tık = ara, tekerlek/−/+ = yakınlaştır |
| `Hik/HikNative.cs` | Tüm P/Invoke imzaları + struct'lar (`incEn` başlıklarıyla satır satır doğrulandı; `PlayM4_RenderPrivateData` ve `NET_DVR_RenderPrivateData` dahil) |
| `Hik/HikRender.cs` | Hareket / akıllı analiz kutularını port (canlıda RealPlay tutamacı da) için aç/kapa |
| `Hik/HikSdk.cs` | Init/Cleanup, exception callback, `Available` |
| `Hik/HikSession.cs` | Cihaz başına tek login; `SdkChannel()` çevirisi |
| `Hik/HikChannelConfig.cs` | Kamera adı SDK ile (§9 "Kamera adı"): `NET_DVR_GET_PICCFG_V40` okuma, uygulamanın **tek** SDK yazması `NET_DVR_SET_PICCFG_V40` (`NET_DVR_SetDVRConfig` yalnız burada, `private` bildirilir; önce `SdkWritePolicy`). Engelleyen çağrılar: UI dışında |
| `Hik/HikLivePlayer.cs` | Canlı SDK oturumu (RealPlay + PlayCtrl) |
| `Hik/HikPlaybackPlayer.cs` | Oynatma SDK oturumu (PlayBackByTime — SDK'ya pencere yok, tek çizici bizim PlayCtrl portu, akış `QueueAhead` ile portumuza göre — + `GetTime` = `PlayM4_GetSystemTime` + `CatchUp` gizli 4× yakalama + MP4 klip + `SyncControls` hız/duraklat) |
| `Hik/HikFrameRecorder.cs` | Kayıt/klip: ikinci PlayCtrl portu (yazılım çözücü, pencere yok) → çözülmüş YV12 → Core `VideoRecorder` (H.264 MP4) |
| `Hik/HikException.cs` | SDK hata kodlarının Türkçe açıklamaları (`Describe`) |

Core tarafı: [`IsapiClient`](src/SPVision.Core/IsapiClient.cs) (Digest auth; cihaz bilgisi,
kamera listesi, kayıt arama, anlık görüntü, şifreleme durumu + doğrulama kodu okuma),
[`Models.cs`](src/SPVision.Core/Models.cs) (Device/Camera/AppSettings; `StreamKeyFor()`:
kamera anahtarı yoksa cihaz anahtarını kullanır), [`SettingsStore`](src/SPVision.Core/SettingsStore.cs)
(DPAPI şifreli JSON), [`StreamUrls`](src/SPVision.Core/StreamUrls.cs) (RTSP adresleri),
[`AppPaths`](src/SPVision.Core/AppPaths.cs) (varsayılan klasörler),
[`LegacyMigration`](src/SPVision.Core/LegacyMigration.cs) (eski addan tek seferlik ayar kopyası),
[`Theme.cs`](src/SPVision.Core/Theme.cs) (`AppTheme`, `ThemeResolver`), [`PlaybackSync.cs`](src/SPVision.Core/PlaybackSync.cs)
(`PlaybackSlots`, `RecordingJump`, `PlaybackSpeed`, `PlaybackSync`, `CalendarMonth`, `PlaybackText` — oynatmanın saf mantığı, birim testli), [`LivePages`](src/SPVision.Core/LivePages.cs) (canlı sayfalar: global yuva sırası, sayfa sayısı/dilim/sınırlama/sarma, kare eşleme), [`CameraList`](src/SPVision.Core/CameraList.cs)
(yenilenen kamera listesinde anahtarı ve "listede göster" seçimini korur), [`DeviceKind`](src/SPVision.Core/DeviceKind.cs)
("NVR · 32 kanal" metni), [`AutoStart`](src/SPVision.Core/AutoStart.cs) (Run değeri), [`AutoStreamQuality`](src/SPVision.Core/AutoStreamQuality.cs) (Oto kalite kuralı),
[`PlaybackRestore`](src/SPVision.Core/PlaybackRestore.cs) (son oynatma düzeni + kameraları) ve [`HttpsPort`](src/SPVision.Core/HttpsPort.cs) (HTTPS kutusu ↔ 80/443 portu, HTTP'ye düşme sorusunun kuralı).
Kayıt (§4 "Kayıt ve klip biçimi"): `Recording/` — `Mp4VideoWriter` (FFmpeg H.264 → MP4; Core bu yüzden `Flyleaf.FFmpeg.ABI`
9.0.0'a ve `AllowUnsafeBlocks`'a bağlıdır), `VideoRecorder` (kuyruk + kodlayıcı iş parçacığı + doğrulama, `RecordingResult`,
`RecordingText`, `Recordings`), `FrameClock`, `Mp4Inspector`. `RecordingTests` sentetik YV12 karelerden MP4 yazıp FFmpeg ile
açar/çözer (depodaki `ffmpeg\` DLL'leri yoksa bu testler bir şey yapmadan geçer).
Cihaz ayarları (§9): [`IsapiWritePolicy`](src/SPVision.Core/IsapiWritePolicy.cs) (yazma izin listesi + yasak kelimeler; riskli işlemlerin tam yol listesi),
3. aşama: [`Maintenance.cs`](src/SPVision.Core/Maintenance.cs) (`MaintenancePaths`, `MaintenancePolicy` tam girişler + kelimeler, `FactoryResetMode`/`FactoryResetText`,
`FirmwareVersion`, `RecorderFirmware`, `CameraFirmware`, `OnlineUpgrade` denetimi, `FirmwareFile` (SHA-256, akışla), `WebUi`), [`RiskyWrites.Maintenance.cs`](src/SPVision.Core/RiskyWrites.Maintenance.cs)
(`MaintenanceWrites` kurucuları, `FirmwareJob`, `ConfirmedRiskyWrite.TakeFirmwareJobAsync`), [`FirmwareRun.cs`](src/SPVision.Core/FirmwareRun.cs) (`RecorderFirmwareRun`, `FirmwareReturn`, `CameraFirmwareBatch`, `FirmwareText`),
[`RiskyWrites.cs`](src/SPVision.Core/RiskyWrites.cs) (`RiskyWriteRequest`, `ConfirmedRiskyWrite`, `IRiskyConfirmation`, `IRiskyTransport`, `RiskyWrites` kurucuları),
[`SdkChannelName.cs`](src/SPVision.Core/SdkChannelName.cs) (`PicCfgV40` yerleşimi, `SdkCharEncoding`, `ChannelNameCodec`, `SdkWritePolicy`, `IPicConfigTransport`, `ChannelRenameWriter` — SDK ile kamera adı),
[`RecorderEvents.cs`](src/SPVision.Core/RecorderEvents.cs), [`RecorderNetwork.cs`](src/SPVision.Core/RecorderNetwork.cs), [`RecorderUsers.cs`](src/SPVision.Core/RecorderUsers.cs) (2. aşama ayrıştırıcıları ve planları),
[`IsapiConfig.cs`](src/SPVision.Core/IsapiConfig.cs) (`IsapiPaths`, `IsapiResponseStatus`, `IsapiXml`, `ConfigEdit`/`SetValueEdit`,
`ConfigWrite`, `ConfigChangeText`, `IConfigTransport`, `ConfigWriter`), [`IsapiClient.Config.cs`](src/SPVision.Core/IsapiClient.Config.cs)
(`GetConfigAsync`, tek yazma yolu `PutConfigAsync`), [`RecorderSettings.cs`](src/SPVision.Core/RecorderSettings.cs) (ayrıştırıcılar,
`MotionSensitivity`, `RecordSchedule` hazır modları, `TimeSyncEdit`, `HikTimeZone`, `RecorderPlan` önce→sonra yazma planı). Yeni ayarlar:
`AppSettings.Theme` (metin olarak saklanır), `ShowMotionBoxes`, `RestoreLastView`, `StartFullScreen`, `LastPlaybackView`; `Camera.Hidden`; `SavedView.Page`;
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

- **Oynatmada SDK'ya pencere verme (21.10.2026):** `VodPara.Wnd` dolu olursa SDK oynatmayı kendi PlayCtrl portuyla da
  aynı pencereye çizer; iki çizici hız değişimlerinden sonra ayrışır ve OSD saati ileri-geri atlar (bkz. §4 "Tek çizici
  kuralı"). Pencere vermeyince akışı veri geri çağrısı ayarlamalıdır: `PlayM4_InputData` başarısızsa paketi **atma**, bekle
  (kısa denemeden sonra atmak bulanık görüntü = paket kaybıydı).
- **Kapanış (21.10.2026, pencere kapanınca süreç sonlanmıyordu; cihazda denenmedi):** eski `Closing` 22 canlı + 16 oynatma
  karesinin SDK akışını **UI iş parçacığında sırayla** bırakıyordu (her biri `StopRealPlay`/`StopPlayBack`, süren bir açılışın
  ağ zaman aşımını bekleyebilir) ve çıkışı WPF'in `OnMainWindowClose` yoluna bırakıyordu: pencere kayboluyor, süreç 40+ sn ya da
  hiç bitmiyordu. **Şimdi** (`MainWindow.OnClosing`): ilk kapatmada `e.Cancel`, pencere **hemen gizlenir**, `App.StartExitWatchdog`
  başlar; `LiveView.ShutdownAsync` + `PlaybackView.ShutdownAsync` tüm kareleri **paralel ve UI dışında** bırakır
  (`VideoTile/PlaybackTile.ReleaseAsync` → `Task.Run(player.Dispose)`; Dispose süren kaydı bitirir ve MP4'ü 5 sn'ye kadar
  bekler), sonra `Recordings.WhenAllFinished`; toplam en çok 5 sn. Ardından **açıkça** `Application.Current.Shutdown()`;
  `App.OnExit` `HikSdk.Shutdown(2 sn)` (`NET_DVR_Cleanup` ayrı iş parçacığında, beklemesi sınırlı). Bekçi: kapanış başladıktan
  8 sn sonra süreç hâlâ yaşıyorsa `logs\shutdown.log`'a yazar ve `Environment.Exit(0)`; o da takılırsa 3 sn sonra
  `Process.Kill`. Her aşamanın süresi `%APPDATA%\SPVision\logs\shutdown.log`'a yazılır.

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
- **Kayıt cihazına yazma kuralı (21.10.2026'dan beri; eski "ISAPI'de yalnızca GET" kuralının yerine):**
  - **Akış şifreleme ve Hik-Connect asla değiştirilmez** (açık kalır) — sahibinin kesin kuralı. SPVision bunları yalnızca okur.
  - Güvenlikle ilgisiz ayarlar **yalnızca Ayarlar › Cihaz ayarları'nda, kullanıcı "Cihaza uygula" + onay** verdikten sonra yazılır;
    sayfa/sekme açılışında, form yüklenirken hiçbir şey yazılmaz.
  - Her yazma iki kapıdan birinden geçer, ikisi de `IsapiWritePolicy`'dedir ve ağa çıkmadan karar verir (birim testli, Türkçe I
    sorunu yok: ordinal karşılaştırma):
    1. **Sıradan ayar:** `IsapiClient.PutConfigAsync` (yalnız PUT) → `IsapiWritePolicy.Check` (açık **izin listesi**). Security / Users /
       Network / reboot / format içeren yol bu kapıdan **asla** geçmez.
    2. **Riskli işlem** (yeniden başlat, disk biçimlendir, ağ adresi, portlar, kullanıcı ekle/şifre/sil): yalnız `IsapiClient.SendRiskyAsync(ConfirmedRiskyWrite)`;
       işlem başına **tam yöntem + tam yol** (`RiskyPatterns`), belirteç yalnız ayrı onay penceresinden çıkar (§9 "2. aşama").
    - **Kalıcı yasak** (iki kapıda da, her yöntemde): encrypt, EZVIZ, HikConnect, Hik-Connect, platform, cloud, factoryReset, upgrade,
      firmware, activate, configurationData, adminAccess, certificate, SRTP, TLS, illegalLogin, loginLock, securityQuestion, ONVIF, shutdown.
      **İstisna 1** (`IsapiWritePolicy.KeywordExemptions`, testli): `adminAccess` kelimesi yalnız `(AdminPorts, PUT, ISAPI/Security/adminAccesses)`
      üçlüsünde (tam yol, ordinal) kalkar ve gövde **port kuralına** bağlıdır (§9 "Portlar"); başka her adminAccess yolu/yöntemi/işlemi yasak kalır.
    - **SDK ile yazma (kamera adı, 21.10.2026):** HCNetSDK'da ayar yazması yalnız `NET_DVR_SetDVRConfig(NET_DVR_SET_PICCFG_V40 = 6180)`'dir; yalnız
      [`HikChannelConfig`](src/SPVision/Hik/HikChannelConfig.cs) içinde `private` bildirilir ve hemen önce
      [`SdkWritePolicy.EnsureSetAllowed`](src/SPVision.Core/SdkChannelName.cs) çalışır: izinli komut listesi yalnız 6180; tampon tam 77140 bayt,
      `dwSize` 77140; **az önce GET edilen tamponla `sChanName` (bayt 4–35) dışında tek bayt farkı yok**; ad geçerli, ad alanında sondaki baytlar 0.
      Başka hiçbir `NET_DVR_Set*` / uzaktan kontrol çağrısı ekleme (tek ayrık istisna: 3. aşamanın yazılım güncelleme çağrıları, yalnız `Hik/HikFirmware.cs` içinde ve yalnız `FirmwareJob` ile — aşağıdaki İstisna 2). Yeni bir SDK yazması gerekirse `SdkWritePolicy` + testleri birlikte genişletilir.
      **İstisna 2 (3. aşama):** `MaintenancePolicy.Entries`'teki dört **tam dizge** (`PUT ISAPI/System/factoryReset?mode=basic`, `…?mode=full`,
      `SDK NET_DVR_Upgrade_V50/ENUM_UPGRADE_DVR`, `SDK NET_DVR_StartRemoteConfig/NET_DVR_UPGRADE_IPC`), yalnız kendi riskli işlemleriyle
      (`FactoryReset`, `RecorderFirmware`, `CameraFirmware`) ve `GÜNCELLE`/`SIFIRLA` yazılı onayla. Başka her upgrade/firmware/factoryReset yolu
      (updateFirmware, upgradeStatus, onlineUpgrade/downloadPackage…, kamera sıfırlama) yasak kalır.
  - Okuma-değiştirme-yazma (`ConfigWriter`): güncel XML GET edilir, yalnızca değişen öğeler değiştirilir, belge bütün olarak PUT
    edilir, `ResponseStatus` kontrol edilir, yeniden okunup doğrulanır; istekler tek tek gider, ilk hatada durulur. Ayrıntı §9.
  - 2. aşama riskli işlemleri **yalnız tam yol** olarak ekledi; kalıcı yasak liste genişledi, hiç kısalmadı. Yeni bir riskli uç nokta
    gerekiyorsa `RiskyOperation` + `RiskyPatterns` + `RiskyWrites` kurucusu + testleri birlikte eklenir.
  - Başka hiçbir yerde ISAPI PUT/POST/DELETE ekleme (kayıt arama `POST ContentMgmt/search` ve takvim `POST …/dailyDistribution` salt
    okuma isteğidir, istisnadır).
  - **Claude (geliştirici) cihaza hiçbir şey yazmaz:** deneme sondaları yalnız ISAPI GET ve SDK `Login_V40` / `Get*` / `Logout` / `Cleanup`
    kullanır; "aynı değeri geri yazmak" da yazmadır. Yazma yolları birim testi, sahte taşıyıcı ve kod incelemesiyle doğrulanır; ilk gerçek
    yazmayı sahibi uygulamadan yapar.
- **Güvenlik (21.10.2026, `IsapiSecurityTests`):** bunları gevşetme.
  - **Yalnız Digest:** `IsapiClient` kimlik bilgisini yalnız `Digest` için kaydeder, `PreAuthenticate` kapalı. Eskiden `Basic` de
    vardı: 401 + `WWW-Authenticate: Basic` döndüren biri (LAN'da araya giren) yönetici şifresini açık metin alırdı. Yalnız Basic
    isteyen cihaz: 401 + "Cihaz yalnızca … Basic kimlik doğrulamasını istedi; şifre gönderilmedi (Digest gerekir)."
  - **Yönlendirme izlenmez:** `AllowAutoRedirect = false`; her 3xx `IsapiException` ("… yönlendirdi …; yönlendirme izlenmez").
    Yoksa 307/308 bir PUT'u gövdesi ve Digest yanıtıyla başka yola (izin listesinin dışına) taşırdı; 3xx gövdesi `ResponseStatus`
    olarak da okunmaz.
  - **Boyut sınırı:** her yanıt en çok 16 MB (`HttpClient.MaxResponseContentBufferSize`, anlık görüntü), XML en çok 4 M karakter
    (`IsapiXml.MaxLength`); aşan yanıt "Cihazın yanıtı beklenenden çok büyük; okunmadı." (bağlantı hatası sayılmaz).
  - **Güvenli XML:** cihazdan gelen her XML yalnız `IsapiXml.Parse` ile okunur (DTD yasak, `XmlResolver` yok, boyut sınırı;
    normal belgede `XDocument.Parse` ile aynı sonuç). Cihaz verisine `XDocument.Parse` / `XElement.Parse` kullanma.
  - **Şifre ekrana/loga yazılmaz:** Flyleaf/FFmpeg hatası RTSP adresini (içinde `kullanıcı:şifre@`) içerebilir. Karelerin `Shorten`'ı
    önce `StreamUrls.RedactCredentials` uygular (her URL'deki `kullanıcı:şifre@` → `***@`), sonra kısaltır. Yeni hata metni/log
    yolunda da aynısını yap. `PlayM4_GetJPEG`'in döndürdüğü boyut tampondan büyükse anlık görüntü başarısız sayılır.
- **Canlıda kare büyütme (çift tık) kareleri ağaçtan çıkarmaz.** `LiveView`'daki 36 `VideoTile` hep `TileGrid`
  çocuğudur (oynatmadaki gibi); düzen ya da sayfa değişince yalnız taşınır/gizlenir (`_tiles` listesinin sırası = konum). Büyütmede diğer kareler `Visibility.Hidden`
  olur: akışları, SDK HWND'leri (`HikVideoHost`) ve Flyleaf yüzeyleri çalışmaya devam eder, gizli kare video
  penceresini de gizler (büyütülen videonun üstüne bir şey binmez). Geri dönüşte görüntü hemen gelir. (Eski hata:
  ızgara `Children.Clear()` ile 1×1'e indiriliyordu → kareler Unloaded → `DisposePlayers` → geri dönüşte yeniden
  açılış + anahtar kare bekleme = birkaç saniye siyah.) **Kareyi ağaçtan çıkarma** — `VideoTile.OnUnloaded`
  oynatıcıyı bırakır. Oto kalitede büyütülmüş durumdayken yalnız büyütülen kare yeniden değerlendirilir (HD'ye
  geçebilir); gizliler eski akışını korur. Büyütülen karenin HD/SD geçişi hâlâ yeniden açılıştır: o kare kısa süre
  siyah kalır (kesintisiz geçiş için ikinci oynatıcı + ikinci HWND gerekir; yapılmadı). Oynatmada büyütme yoktur.

## 9. Ayarlar › Cihaz ayarları (kayıt cihazına yazma, 1. aşama — 21.10.2026)

> Henüz cihazda yazma denenmedi; ilk gerçek yazmayı sahibi uygulamadan yapacak (en güvenlisi: tek kamerada hareket algılamayı kapat/aç).
> Uç noktalar DS-8632NI-I8 (V4.61.025) üzerinde **yalnızca GET** ile bulundu; 22 kanalın hepsinde aşağıdakilerin tümü 200 döndü.

**Sekmeler:** Genel bakış · Kameralar · Hareket · Kayıt · Sistem (1. aşama); Olaylar · Ağ · Kullanıcılar + Sistem'de yeniden başlatma /
biçimlendirme (2. aşama, aşağıda); Bakım: yazılım güncelleme ve fabrika ayarları (3. aşama, aşağıda — yalnız tam dizge girişleri, yazılı onayla).
Her sekme ilk gösterildiğinde okunur (GET); sayfa her açılışta cihazı yeniden okur.
Okunamayan değer "okunamadı", cihazın sunmadığı ayar "Bu kamera desteklemiyor" yazar (403/404/405/501) — hata penceresi yok.

| Sekme | Okuma (GET) | Yazma (PUT, izin listesinde) |
|---|---|---|
| Genel bakış | `System/deviceInfo`, `ContentMgmt/InputProxy/channels` (+`/status`, `/capabilities` → toplam kanal `id@max`), `System/time`, `ContentMgmt/Storage/hdd`, `System/Network/EZVIZ` (yalnızca `enabled` + `streamEncrypteEnabled`; doğrulama kodu bu sayfada okunmaz) | — |
| Kameralar | `ContentMgmt/InputProxy/channels/{n}/video/overlays`, `Image/channels/{n}` (+`/capabilities`, `Color/*Level` min/max), `ContentMgmt/StreamingProxy/channels/{n}01` ve `{n}02` (+`/capabilities`) | kanal adı ISAPI ile **yazılmaz**, **SDK ile** yazılır (aşağıdaki "Kamera adı"), OSD aynı overlays yolu (`DateTimeOverlay/enabled`, `channelNameOverlay/enabled`), görüntü `Image/channels/{n}` (`Color/brightnessLevel`, `contrastLevel`, `saturationLevel`), akış `StreamingProxy/channels/{n}0{1,2}` (`Video/videoResolutionWidth`+`Height` birlikte, `maxFrameRate` = fps×100, 0 = tam kare hızı; bit hızı VBR'de `vbrUpperCap`, CBR'de `constantBitRate`; `videoCodecType`) |
| Hareket | `ContentMgmt/InputProxy/channels/{n}/video/motionDetection` (+`/capabilities`: `sensitivityLevel` 0–100 adım 20) | aynı yol: `enabled`, `MotionDetectionLayout/sensitivityLevel` (Düşük 20 · Orta 60 · Yüksek 80; okurken 0–20 Düşük, 40–60 Orta, 80–100 Yüksek — `MotionSensitivity`) |
| Kayıt | `ContentMgmt/record/tracks/{n}01` | aynı yol: açık/kapalı = `CustomExtensionList/CustomExtension/enableSchedule`; mod = `TrackSchedule/ScheduleBlockList/ScheduleBlock` içindeki 7 tam gün `ScheduleAction` (`ActionRecordingMode` CMR = Sürekli, MOTION = Yalnızca hareket; eylem yok = Kapalı); `PreRecordTimeSeconds` / `PostRecordTimeSeconds`. Bunlara uymayan program **Özel program**dır, kullanıcı açıkça bir mod seçmedikçe yazılmaz |
| Sistem | `System/deviceInfo`, `System/time`, `System/time/ntpServers`, `ContentMgmt/Storage/hdd` | cihaz adı `System/deviceInfo` (`deviceName`); "Bilgisayarla eşitle" `System/time` (`timeMode`=manual, `localTime` = bu PC'nin saati + ofseti, `timeZone` farklıysa `CST-3:00:00` biçimi — işaret ters). NTP yalnızca gösterilir; yeniden başlatma ve disk başına biçimlendirme 2. aşamada (riskli işlem) |

**Kanal numaraları:** ISAPI kanal no 1–22 (InputProxy), akış/kayıt izi `kanal×100 + 1/2` (101, 102 … 2201). SDK'daki 33+ numaraları burada **kullanılmaz**.

**Yazma akışı:** form değeri ↔ son okunan değer farkı `RecorderPlan` ile `ConfigWrite` listesine çevrilir → "Cihaza uygula" her değişikliği
"<kamera> · <alan>: <eski> → <yeni>" satırıyla listeleyen onay ister (`ConfirmDialog` liste aşırı yüklemesi) → `ConfigWriter` her belgeyi
sırayla GET → yalnızca değişen öğeleri değiştir (XDocument, ad alanı ve diğer öğeler korunur) → PUT → `ResponseStatus` (`statusCode` 1 = OK;
diğerleri Türkçe açıklamayla hata, ör. "Geçersiz işlem (notSupport: cihaz bu ayarı desteklemiyor)") → yeniden GET + doğrula. İlk hatada durur;
kartta "Uygulandı" ya da "Cihaz değeri kabul etmedi: …" yazar, hata varsa UYGULANDI / UYGULANMADI listesi gösterilir; ardından sekme cihazdan
yeniden okunur. "Geri al" formu son okunan değerlere döndürür (cihaza bir şey göndermez). Gövdede `password`/`encrypt`/`EZVIZ`/`platform`/
`verificationCode` adlı öğe varsa PUT reddedilir.

**Kamera adı (2. aşamada kapatıldı):** 1. aşama adı `InputProxy/channels/{n}` belgesini bütün olarak geri yazıyordu. O belge kameranın
**bağlantı kaydıdır** (`sourceInputPortDescriptor`: IP, yönetim portu, kullanıcı adı, seri no, `certificateValidationEnabled`, `devIndex`) ve
DS-8632NI-I8'de GET'te `password` öğesi **hiç gelmez** (capabilities'te de yok). Şifresiz geri yazmanın sonucu doğrulanamıyor: en iyi
durumda kamera yeniden bağlanır, en kötüsünde şifresi boşalır → çevrimdışı, kayıt yok. Şifre içermeyen bir ad uç noktası yok
(`System/Video/inputs/channels/{n}` 403 `invalidOperation`, `channelNameOverlay`'de ad öğesi yok). **Karar:** `InputProxy/channels/{n}`
izin listesinden **kalıcı olarak** çıkarıldı, `RecorderPlan.Camera` adı planlamaz, sıradan gövdede `sourceInputPortDescriptor` öğesi de reddedilir (testli).

**Kamera adı SDK ile (21.10.2026, cihazda yazma denenmedi):** ad HCNetSDK resim ayarı `NET_DVR_PICCFG_V40.sChanName`'den okunur/yazılır; bu yapıda
kameranın bağlantı bilgisi (IP, kullanıcı, şifre) **yoktur**. Kanal = `HikSession.SdkChannel(n)` (bu cihazda 33 + n − 1). Salt okuma sondası
(22 kanal): `NET_DVR_GET_PICCFG_V40` (6179) hepsinde başarılı, dönen boyut ve `dwSize` **77140** (başlıktan hesaplanan `sizeof` ile aynı; 77136'lık
tampon hata 17 ile reddedildi), aynı kanalın iki okuması bayt bayt aynı; adlar ISAPI'deki ve uygulamadaki adlarla 22/22 aynı (hepsi ASCII). Giriş
`byCharEncodeType` = **6 (UTF-8)**. Kodlama kuralı (`SdkCharEncoding`, `ChannelNameCodec`): 0 bilinmiyor (yalnız ASCII ad yazılır, ASCII dışı ad
gösterilmez), 1/2 GBK, 3 BIG5, 4 Shift_JIS, 5 EUC-KR, 6 UTF-8, 7–21 ISO-8859-(n−6) (Türkçe cihaz 15 = ISO-8859-9 olabilir). Sınır **32 bayt** o
kodlamada (UTF-8'de Türkçe harf 2 bayt; sonlandırıcı gerekmez, okurken ilk NUL'a kadar); boş, kontrol karakteri ve `< > & " ' % \` reddedilir;
ad kırpılır. Yazma (Kameralar › Cihaza uygula, sıradan onay penceresi, satır "<kamera> · Kamera adı: eski → yeni"): ISAPI yazmalarından sonra,
UI dışında `ChannelRenameWriter`: GET → cihazdaki ad hâlâ gösterilen ad mı (değilse gönderilmez) → `ChannelNameCodec.WithName` (yalnız bayt 4–35,
ad + sıfır dolgu) → `SdkWritePolicy` → SET → GET ile doğrula; ilk hatada durur, UYGULANDI/UYGULANMADI raporu ISAPI raporuyla birleşir. Başarılı
adlar SPVision ayarlarına da yazılır (`CamerasRenamed`: kaydet + kenar çubuğu/oynatma listesi + kare başlıkları, akış yeniden açılmaz). SDK okunamazsa
(SDK yok, giriş başarısız — cihaz başına bir kez denenir —, GET hatası, çözülemeyen kodlama) alan salt okunur kalır ve yardım satırında kısa sebep yazar.

### 2. aşama: Olaylar · Ağ · Kullanıcılar · yeniden başlatma · biçimlendirme (21.10.2026, cihazda yazma denenmedi)

Uç noktalar DS-8632NI-I8'de yalnız **GET** ile doğrulandı (yeniden başlatma / biçimlendirme / kullanıcı yazma uç noktalarına GET bile
atılmadı: bunlar standart ISAPI yollarıdır, `System/capabilities` ve `Storage/hdd/capabilities` okunarak desteklendiği görüldü).
Cihazın sunmadığı ayar "Bu cihaz desteklemiyor" ya da "—" (ipucu ile) gösterir, hata penceresi açılmaz.

| Sekme | Okuma (GET) | Yazma |
|---|---|---|
| Olaylar | `Event/triggersCap` (`*TriggerCap/isSupportBeep/Center/Email`), `Event/triggers` (tüm tetikler tek belgede), kamera başına `ContentMgmt/InputProxy/channels/{n}/video/videoLoss` ve `/tamperDetection` (`enabled`) | **sıradan** (ConfigWriter): kamera aç/kapa aynı iki yol (`enabled`); tepkiler `Event/triggers/{id}` — `videoloss-{n}`, `tamper-{n}`, `diskfull`, `diskerror`, `nicbroken`, `ipconflict`, `illaccess`, `recordingfailure`; `EventTriggerNotificationList`'e `<EventTriggerNotification><id>beep</id><notificationMethod>beep</notificationMethod>` ekle/çıkar (`NotificationEdit`; diğer bildirimler, ör. `record`, dokunulmaz). Kamera satırı "Görüntü kaybı / Kurcalama" tüm kameraların tetiklerine uygulanır; yalnız farklı olan tetik yazılır; karışıksa satırda "bazı kameralarda farklı" |
| Ağ | `System/Network/interfaces` (IPv4: `addressingType`, `ipAddress`, `subnetMask`, `DefaultGateway/ipAddress`, `PrimaryDNS`/`SecondaryDNS`), `System/Network/Bond` (açıksa düzenleme kapalı), `Security/adminAccesses` (+ `/capabilities` → port min/max/def; cihazda HTTP 80, RTSP 554, HTTPS 443 + TLS ayarları, DEV_MANAGE 8000, WebSocket 7681, IOT 30999, SDK_OVER_TLS 8443) | **riskli**: `PUT System/Network/interfaces/{id}/ipAddress` — SPVision'ın bağlandığı arabirim (adresi `Device.Host` olan, yoksa `defaultConnection`). Onaydan hemen önce belge yeniden GET edilir; değişmişse durur. Gövde = o belge + değişen öğeler. Doğrulama `NetworkPlan.Validate` (geçerli IPv4, /8–/30 maske, ağ/yayın adresi değil, ağ geçidi aynı alt ağda). Başarılı ya da **yanıtsız** sonuçtan sonra eski statik adres `Device.Host` ise "SPVision yeni adresten bağlansın mı?" → `Device.Host` güncellenir, kaydedilir, SDK oturumu kapanır, kareler yeniden bağlanır. **Portlar** (ayrı kart, **riskli**): aşağıdaki "Portlar" |
| Kullanıcılar | `Security/users` (yalnız `id`, `userName`, `userLevel` okunur; listede şifre yoktur, istenmez), `Security/capabilities` (`supportUserNums` = 32) | **riskli**: ekle `POST Security/users`; şifre değiştir `PUT Security/users/{id}`; sil `DELETE Security/users/{id}` (id ≥ 2). Gövde yalnız `User{id,userName,password,userLevel,loginPassword}` (izin/bağlama/enable öğesi yok); yeni kullanıcı yalnız Operatör/İzleyici; yönetici (id 1 ya da Administrator) silinemez (politika + kurucu + düğme). `loginPassword` = SPVision hesabının şifresi, formda yeniden sorulur ve kayıtlı şifreyle karşılaştırılır (yanlışsa cihaza gitmez — cihazın giriş kilidini tetiklemesin). Şifre kuralı `UserRules` (8–16, ASCII, 4 türden ≥ 2, kullanıcı adını içermez, iki kez). Şifre taşıyan gövde **yalnız HTTPS** ile gider (`SendRiskyAsync` HTTP'de reddeder). SPVision'ın kendi hesabının şifresi değişince `Device.Password` güncellenir → `SettingsStore` (DPAPI) ile kaydedilir → yeniden bağlanır. `Security/users/capabilities` bu cihazda 503 (meşgul) döndü; kullanılmıyor |
| Sistem | (1. aşama) | **riskli**: `PUT System/reboot` (gövdesiz); disk başına `PUT ContentMgmt/Storage/hdd/{id}/format` (gövdesiz; onayda disk adı, kapasite, durum; `BİÇİMLENDİR` yazılmadan Biçimlendir düğmesi açılmaz). Biçimlendirme sonrası diskler yeniden okunur |

**Riskli yazma mekanizması (`RiskyWrites.cs`):**
1. `RiskyWrites.Reboot / FormatDisk / NetworkAddress / AdminPorts / AddUser / ChangePassword / DeleteUser` bir `RiskyWriteRequest` kurar: işlem türü, **tam
   yöntem + yol**, gövde (bir kez dizgeye çevrilir: onaylanan = gönderilen), başlık, uyarı, eski → yeni satırları (şifre **asla** yok), gerekiyorsa
   yazılacak kelime. Kurucu `IsapiWritePolicy.EnsureRiskyAllowed` + `EnsureRiskyBodyAllowed`'ı hemen çalıştırır.
2. `ConfirmedRiskyWrite.Confirm(request, IRiskyConfirmation)` — tek uygulama `RiskyConfirmDialog` — kullanıcı onaylarsa (ve kelime Türkçe büyük
   harfe çevrilerek `BİÇİMLENDİR`'e eşitse) belirteç verir. Belirtecin kurucusu `private`'tır; başka yoldan elde edilemez.
3. `IsapiClient.SendRiskyAsync(token)`: belirteci **bir kez** tüketir (ikinci kullanım ya da 2 dakikadan eski onay → `IsapiWriteDeniedException`, istek
   gitmez), yöntem/yol/gövdeyi politikaya yeniden sorar, şifreli gövde için HTTPS ister, sonra yalnızca o isteği gönderir. `PutConfigAsync`
   riskli yollara hiçbir koşulda gitmez (Security/Users/Network/reboot/format sıradan kapıda yasak).
4. Görünümde `RunRiskyAsync` (UI iş parçacığını bloklamaz): onay → gönder → `Done` / `Refused` (Türkçe `ResponseStatus`) / `NoAnswer` (bağlantı
   koptu: yeniden başlatma ve adres değişiminde beklenebilir; "yapılmış olabilir" denir). `statusCode` 7 (yeniden başlatma gerekli) başarı sayılır.

**Portlar (Ağ sekmesi, riskli, 21.10.2026, cihazda yazma denenmedi):** dört kutu (HTTP, HTTPS, RTSP, Sunucu (SDK); 240 px, adres alanlarıyla aynı
satır düzeni, AutomationId `NetworkPortHttp/Https/Rtsp/Server`), kartın kendi Geri al / Cihaza uygula düğmeleri ve sonuç satırı. Kurallar:
- Tek izin: `RiskyOperation.AdminPorts` + `PUT ISAPI/Security/adminAccesses` (tam yol; `/`, büyük-küçük harf, `%2F`, sorgu, `/1`, `/capabilities`
  değişkenleri reddedilir). Belirteç (`ConfirmedRiskyWrite`) olmadan gitmez; `PutConfigAsync` bu yola hiç gitmez.
- Gövde kuralı `IsapiWritePolicy.CheckAdminPortsBody(okunan, gövde)` — kurucuda ve `SendRiskyAsync`'te yeniden (istek okunan belgeyi `Original`
  olarak taşır): aynı öğeler aynı sırada, aynı ad/ad alanı/nitelikler, her metin aynı; **yalnız** `protocol`'ü HTTP / HTTPS / RTSP / DEV_MANAGE olan
  (listede tek) `AdminAccessProtocol`'ün `portNo`'su değişebilir, yeni değer düz sayı 1–65535 ve başka bir girdinin (WebSocket, IOT, SDK_OVER_TLS dahil)
  portu değil; en az bir port değişmeli; `password`/`secret`/`privateKey`/`verificationCode`/`encrypt`/`EZVIZ`/`platform`/`cloud` öğesi olamaz. TLS,
  `enabled`, `redirectToHttps`, eklenen/silinen/yer değiştiren protokol → reddedilir (testli: `AdminPortsTests`).
- `PortPlan.Validate`: 1–65535, cihazın `capabilities` aralığı (bu cihazda RTSP 1024–65535, DEV_MANAGE 2000–65535; protokolün varsayılanı —
  `def`/`default` ya da 80/443/554/8000 — her zaman kabul: geri alınabilsin), tüm girdilerde benzersiz. `PortPlan.Warnings`: iyi bilinen portlar
  (22 SSH, 3389, 445, 8080…), başka protokolün olağan portu ve <1024 → onay penceresinde "Dikkat:" (engellemez).
- Akış: form → doğrula → belge **onaydan hemen önce** yeniden GET (sayfadakiyle `XNode.DeepEquals` değilse gönderilmez) → `RiskyWrites.AdminPorts`
  (eski → yeni satırları; uyarı: SPVision, web arayüzü, iVMS-4200, telefon uygulamaları, yönlendiricideki port yönlendirme yeni portu ister) →
  onaydan **sonra, PUT'tan hemen önce** bir GET daha (`RunRiskyAsync` `beforeSend`; değişmişse gönderilmez) → PUT. HTTPS portu istek sırasında
  değişip yanıt gelmezse `NoAnswer` ("değişmiş olabilir"). `Done`/`NoAnswer` sonrası SPVision'ın kullandığı port değiştiyse
  (`PortPlan.FollowDevice`: HTTPS kullanılıyorsa HTTPS, değilse HTTP → `Device.HttpPort`; RTSP → `RtspPort`; DEV_MANAGE → `SdkPort`; yalnız eski
  değer SPVision'ınkiyle aynıysa) "yeni portlardan bağlansın mı?" → `DeviceUpdated(device, true)` (kaydet, SDK oturumu kapanır, yeniden bağlanır).

**İzin listesine eklenenler (tam yol):** sıradan: `ContentMgmt/InputProxy/channels/{n}/video/videoLoss`, `…/video/tamperDetection`,
`Event/triggers/(videoloss|tamper)-{n}`, `Event/triggers/(diskfull|diskerror|nicbroken|ipconflict|illaccess|recordingfailure)`. Riskli:
`PUT System/reboot`, `PUT ContentMgmt/Storage/hdd/[1-99]/format`, `PUT System/Network/interfaces/[1-9]/ipAddress`, `POST Security/users`,
`PUT Security/users/[1-99]`, `DELETE Security/users/[2-99]`, `PUT Security/adminAccesses` (yalnız port gövdesi). SDK: yalnız
`NET_DVR_SET_PICCFG_V40` (yalnız ad baytları). **Çıkarılan (kalıcı):** `ContentMgmt/InputProxy/channels/{n}`. Yol karakterleri yalnız
`A-Z a-z 0-9 / -` (sorgu, `%`, `..`, ters bölü, baştaki/sondaki `/`, `//` reddedilir). Testler: `RiskyWritePolicyTests` (izinli/yasak her yol,
yöntem/işlem çaprazı, büyük-küçük harf, sondaki `/`, `..`, `%2F`, sorgu, Türkçe kültür, gövde sözlüğü, belirteç tek kullanım / süre / kelime,
HTTP'de şifre yok), `RecorderPhaseTwoTests` (ayrıştırıcılar, planlar, ağ doğrulaması, kullanıcı kuralları), `AdminPortsTests` (port yolu, gövde farkı, `PortPlan`), `SdkRenameTests` (ad kodlaması, 32 bayt, bayt farkı koruması, `SdkWritePolicy`, `ChannelRenameWriter`).

### 3. aşama (Bakım): yazılım güncelleme · fabrika ayarları (21.10.2026, cihazda HİÇ yazma denenmedi)

> **Gerçek güncelleme ve sıfırlamayı yalnız sahibi yapar.** Kod yazılırken cihaza tek bir PUT/POST/DELETE, yükleme, sıfırlama ya da SDK
> Set/Upgrade çağrısı gönderilmedi; upgrade/updateFirmware/factoryReset/reboot adreslerine GET bile atılmadı. Okunanlar (yalnız GET):
> `System/deviceInfo` (DS-8632NI-I8, **V4.61.025 build 220905**), `System/capabilities` (`isSupportPostUpdateFirmware` **yok**,
> `isSupportOnlineUpgradeTask` true, `isSupportInputProxyDeviceInfo` true), `System/onlineUpgrade/capabilities` (200; `isSupportUpgradeChannels`
> true), `onlineUpgrade/server` (`connectStatus` true), `onlineUpgrade/status`, `onlineUpgrade/version?check=false` (parametresiz 400
> `badParameters`; `newVersionAvailable` false), `ContentMgmt/InputProxy/channels` (kamera başına `sourceInputPortDescriptor/model` +
> `firmwareVersion` — 22 kamera, 4 model: DS-2CD1043G0-IUF, DS-2CD1323G0-IUF, DS-2CD1343G0-IUF, DS-2CD1343G2-LIUF; sürümler V5.5.800 … V5.8.4),
> `…/channels/status` (`online`), `…/channels/1/deviceInfo` (200; kullanılmıyor, liste yetiyor).

**Kaynak seçimi (yalnız `sdk/…/doc` ve `incEn`; `reversing/raw/` okunmadı):** ISAPI dosya yükleme uç noktası (`updateFirmware`) yerel SDK kılavuzunda
belgeli değil ve bu cihaz `isSupportPostUpdateFirmware` bildirmiyor → **kayıt cihazı yazılımı SDK ile**: `NET_DVR_Upgrade_V50`
(`NET_DVR_UPGRADE_PARAM`, `ENUM_UPGRADE_DVR`; General Developer Guide §4.2 "Remotely Upgrade Device", §12.7.5–8), ilerleme
`NET_DVR_GetUpgradeProgress` (0–100), durum `NET_DVR_GetUpgradeState` (Tablo 12-3: 1 bitti, 2 sürüyor, 3 başarısız, 4 ağ koptu/bilinmiyor, 5 dil,
6 flash, 7 paket türü (yanlış model), 8 paket sürümü, 9–11 destek), adım `NET_DVR_GetUpgradeStep` (STEP_*), sonra `NET_DVR_CloseUpgradeHandle`.
SDK dosyayı **diskten kendisi okur** (bellekte tutulmaz). **Kamera yazılımı kayıt cihazı üzerinden SDK ile**: `NET_DVR_StartRemoteConfig(NET_DVR_UPGRADE_IPC = 6174,
NET_DVR_UPGRADE_IPC_PARAM { NET_DVR_STREAM_INFO[64], szFileName[260] })` (resmi ClientDemo `DlgUpgradeIPC.cpp`), `dwChannel` = SDK kanal no
(`HikSession.SdkChannel`, 33+), geri çağırma durumları `NET_SDK_CALLBACK_STATUS_*` (1000 başarı, 1001 sürüyor, 1002 başarısız, 1003 istisna, 1004 dil,
1005 cihaz türü), kanal hatası `NET_SDK_UPGRADE_IPC_ERR_CODE`; **her çağrıda tek kamera**, sırayla. **Fabrika ayarları ISAPI**: `PUT ISAPI/System/factoryReset?mode=`
(E.177; gövdesiz, yanıt ResponseStatus). Belgeye göre: `full` = tüm ayarlar; `basic` = **ağ ayarları hariç** tüm ayarlar; `other` = ağ ve kullanıcılar hariç;
`part` = kısmi. SPVision yalnız `basic` ve `full` gönderir; belge `basic`'in kullanıcıları koruduğunu **söylemez** (arayüz bunu açıkça yazar).

| Kart | Okuma | Yazma (riskli, yazılı onay) |
|---|---|---|
| Kayıt cihazı bilgisi | `System/deviceInfo` (model, `firmwareVersion` + `firmwareReleasedDate` "build yyMMdd"; seri/MAC/ID tutulmaz) | — ; "Web arayüzünü aç" = `WebUi.Address(Device)` (cihaz ayarındaki şema + port; 443/80 yazılmaz, IPv6 köşeli). Başlıktaki düğme de aynısını kullanır |
| Kayıt cihazı yazılımı | "Denetle": `onlineUpgrade/capabilities` (yoksa "Bu cihaz çevrimiçi denetimi desteklemiyor — Hikvision sitesinden dosya indirin" + "Hikvision sitesi" düğmesi) → `onlineUpgrade/server` (`connectStatus` false → sunucuya ulaşılamıyor) → `onlineUpgrade/version?check=true` (cihaz Hikvision sunucusuna sorar; GET). **Hiçbir şey indirilmez/kurulmaz**; yeni sürüm varsa yalnız yazılır | "Dosyadan yükle…" (.dav/.digicap, 1 MiB–512 MiB, SHA-256 akışla) → "Güncelle…" → onay (`GÜNCELLE`; model, yüklü sürüm, dosya adı/boyutu, SHA-256) → `TakeFirmwareJobAsync` (belirteci tüketir, dosyayı yeniden özetler) → `RecorderFirmwareRun` (1 sn'de bir yoklar; ilerleme çubuğu; 10 dk ilerleme yoksa ya da 60 dk'da "durum bilinmiyor", tutamaç kapatılır) → bitti ise `FirmwareReturn` (10 sn'de bir deviceInfo GET; yeni sürüm / sürüm değişmedi / 5 dk'da yeniden başlamadı → "Sistem › Yeniden başlat" / 20 dk'da dönmedi) |
| Kamera yazılımları | `InputProxy/channels` + `/status` → tablo (☐ · KANAL · KAMERA · MODEL · YÜKLÜ SÜRÜM · DURUM); yalnız **çevrimiçi ve HIKVISION protokollü** kamera seçilebilir; "Yenile" yeniden okur | "Dosyadan yükle…" → "Güncelle (n)…" → onay (`GÜNCELLE`; her kamera satırı, **farklı modeller seçiliyse uyarı**, ilk hatada dur açık/kapalı) → `CameraFirmwareBatch`: kameralar **sırayla**, her biri yükle → kameranın geri gelmesini bekle (`InputProxy` GET; yeni sürüm = başarı); "İlk hatada dur" varsayılan **açık** → kalanlar "Başlatılmadı". Satır durumları yerinde güncellenir |
| Fabrika ayarları (kırmızı kart) | — | "Temel sıfırla…" `PUT …factoryReset?mode=basic`, "Tam sıfırla…" `…?mode=full`; onay `SIFIRLA`, **kırmızı** bant: akış şifreleme ve Hik-Connect de varsayılana döner, yeniden etkinleştirme gerekebilir, SPVision bağlantıyı kaybeder; SPVision bunları sonradan **kendisi açmaz/değiştirmez** |

**Politika (3. aşama):** `RiskyOperation`'a `FactoryReset`, `RecorderFirmware`, `CameraFirmware` eklendi. `IsapiWritePolicy.CheckRisky` bu üç işlem için
`MaintenancePolicy.Check`'e gider: dört girişten biriyle **ordinal tam eşitlik** (desen yok → büyük/küçük harf, sondaki `/`, `%2F`, `..`, ek/yinelenen sorgu
parametresi, yanlış mod, boşluk hepsi reddedilir). Eski işlemler bu hedeflere, yeni işlemler başka hedeflere ulaşamaz; kalıcı yasak listesi aynen durur.
Yazılım işlemlerinin "yöntemi" `SDK`'dır: `IsapiClient.SendRiskyAsync` bu yöntemi reddeder (belirteç tüketilir, istek gitmez); SDK koşucusu (`Hik/HikFirmware.cs`)
yalnız `FirmwareJob` alır, `FirmwareJob`'u yalnız `ConfirmedRiskyWrite.TakeFirmwareJobAsync` üretir (tek kullanım, 2 dk, işlem türü + giriş yeniden denetlenir,
dosya boyutu + son yazma zamanı + SHA-256 onaydakiyle aynı olmalı). Üç işlem de gövde göndermez (`EnsureRiskyBodyAllowed`). Kelimeler Türkçe büyük harfe
çevrilerek karşılaştırılır: `güncelle` ✓, `GUNCELLE` ✗; `sıfırla` ✓, `sifirla` ✗ (→ `SİFİRLA`). SDK yolu ANSI kod sayfasına sığmayan ya da 260 baytı aşan dosya
yolunu reddeder ("kısa, Türkçe karaktersiz klasöre taşıyın"). Güncelleme sürerken sekmeler/cihaz seçici kilitli, pencere kapatılmak istenirse soru sorulur.
Yeni tema anahtarları: `DangerSoftBg`, `OnDanger` (Light/Dark); `RecorderControls.xaml`'da `SettingsDangerFillButton` (120×30, kırmızı dolgu).
Testler: `MaintenanceTests` (izinli 4 giriş × diğer işlemler/yöntemler, ~30 reddedilen sıfırlama varyantı, yanlış SDK hedefi/yöntemi, diğer upgrade uç noktaları,
güvenlik sözlüğü yeni işlemlerde de kapalı, gövdesizlik, kelimeler, belirteç tek kullanım/süre, dosya değişince iş yok, yazılım belirteci ISAPI'ye gitmez,
kamera isteği kuralları + farklı model uyarısı, ayrıştırıcılar (sır tutulmaz), çevrimiçi denetim yalnız okur, koşucu durumları/takılma, geri dönüş bekleme,
kamera sırası + ilk hatada dur, web adresi).

**Açık sorular (yalnız gerçek yazmayla doğrulanabilir):** (1) `NET_DVR_Upgrade_V50` sonrası DS-8632NI-I8 kendiliğinden yeniden başlıyor mu (ClientDemo
"upgrade successfully, reboot please" diyor; SPVision 5 dk bekler, başlamazsa Sistem › Yeniden başlat'ı önerir). (2) `NET_DVR_UPGRADE_IPC` geri çağırmasında
durum `dwType`'ta mı (demo) yoksa tamponda mı (genel sözleşme) — ikisi de kabul ediliyor. (3) `mode=basic` bu cihazda kullanıcıları/yönetici şifresini
koruyor mu ve akış şifreleme/Hik-Connect'i gerçekten sıfırlıyor mu. (4) `onlineUpgrade/version?check=true` yalnız `check=false` ile denendi.

## 10. Erişilebilirlik: denetimler UI Automation ile de çalışmalı (21.10.2026)

- Her denetim fare, klavye **ve UI Automation** (ekran okuyucu, `TogglePattern.Toggle()`, `SelectionItemPattern.Select()`,
  `InvokePattern.Invoke()`) ile aynı şeyi yapmalı. **Tuzak:** düz `ToggleButton`/`CheckBox` UIA ile değiştirilince `IsChecked`
  değişir ama `Click` **gelmez** → yalnız `Click`'e bağlı davranış çalışmaz, denetim seçili görünür ama görünüm değişmez.
- Davranışı `Click`'te olan, `IsChecked`'i kodun kendisi yöneten düğmeler (sekmeler, Ayarlar dişlisi, kalite/tema/cihaz ayarı
  segmentleri, hareket kutuları düğmesi, düzen seçici öğeleri, takvim günleri, oynatma kamera satırları, cihaz listesi satırları,
  cihaz ayarları sekmeleri) [`ClickToggleButton`](src/SPVision/Controls/ClickToggleButton.cs) kullanır: UIA Toggle = gerçek tık
  (`OnClick`: değiştir + `Click`), fare/klavye davranışı aynen kalır, çift çalışma olmaz. Yeni böyle düğme eklerken düz
  `ToggleButton` kullanma (stiller `TargetType="ToggleButton"` olduğu için olduğu gibi uyar; stil açıkça verilmeli).
- Durum anahtarları (`ToggleSwitch` onay kutuları, HTTPS kutusu, "Bilgisayarla eşitle", geçici Popup düğmeleri —
  `TransientPopup`) `Checked`/`Unchecked`'e bağlıdır; kodun yüklerken koyduğu değer bayrakla (`_loadingSwitches`) ya da
  olaylar başlangıç değerinden **sonra** bağlanarak yok sayılır. Ayarlar sol menüsü `RadioButton` + `Checked` (zaten uyumlu).
- Etkileşimli her denetimin anlamlı `AutomationProperties.Name`'i olmalı; ana denetimlerde `x:Name` (= AutomationId) bulunur,
  kodda üretilenlerde `AutomationProperties.AutomationId` verilir (`Day21`, `Layout16`, `RecorderTabMotion`). Takvim günü adı
  "21 Ekim 2026[, kayıt var]", yardım metni tık / çift tık / Enter'ı anlatır.
