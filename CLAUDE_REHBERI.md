# SPVision — Claude için Tam Rehber

> **Bu belgeye ek olarak `PROJE_DURUMU.md`, `GELISTIRICI_GOREVI.md`, `README.md` ve
> `reversing/BULGULAR.md` de klasördedir.** Bu dosya hepsinin özetidir; ayrıntı için
> ilgili belgeye bak. Kodun tamamı bu klasörde çalışır durumdadır: 0 derleme hatası,
> 189/189 birim testi geçer, **gerçek cihazda (Hikvision DS-8632NI-I8) test edilmiştir.**
> (21.10.2026'daki düzen seçici + eş zamanlı çoklu oynatma değişikliği ile cihaz düzenleyicideki HTTPS varsayılanı / HTTP'ye düşme sorusu henüz cihazda denenmedi; bkz. §4, §5.)
> (Canlı izlemede sayfalar — §4 "Canlı izlemede sayfalar" — da henüz cihazda denenmedi.)
> (Ayarlar › Cihaz ayarları'ndan kayıt cihazına yazma — §9 — henüz cihazda denenmedi; okuma uç noktaları cihazda GET ile doğrulandı.)
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
| `tests/SPVision.Core.Tests/` | 189 xUnit testi (`dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj`) |
| `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` | **Hikvision resmi SDK (lisanslı, repoya konmaz)** — DLL'ler `lib/`, başlıklar `incEn/`, resmi C# örnekleri `C# demo/`, C++ örnekleri `ClientDemo/`, kılavuzlar `doc/` |
| `ffmpeg/` | FFmpeg 9 paylaşımlı DLL'leri (FlyleafLib bunları kullanır) — `tools/get-ffmpeg.ps1` ile yeniden indirilebilir |
| `reversing/` | Eski iVMS'in tersine mühendislik bulguları (`BULGULAR.md` — CPU sorununun kökeni) |
| `tools/` | `get-ffmpeg.ps1` vb. yardımcı betikler |

## 3. Derleme ve çalıştırma (Windows 11, .NET 10 SDK, x64)

```powershell
cd SPVision
dotnet build SPVision.slnx          # 0 hata / 0 uyarı beklenir
dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj   # 189/189 geçer
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
  pencerenin tam ortasında; tek sayfada ve canlı dışında gizli. Oklar ve PageUp/PageDown (yalnız canlıda, metin kutusu
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
- **Oynatmada hareket kutuları (21.10.2026 düzeltmesi):** `NET_DVR_PlayBackByTime_V40`'a da pencere verilir
  (`VodPara.Wnd`), yani SDK oynatmayı **kendi PlayCtrl portuyla** da aynı pencereye çizer — canlıdaki RealPlay
  çizicisinin karşılığı. Eskiden yalnız bizim port kapatılıyordu, SDK'nın portu kutuları çizmeye devam ediyordu.
  Şimdi o port `NET_DVR_GetPlayBackPlayerIndex(playHandle)` ile bulunur ve aynı `PlayM4_RenderPrivateData` anahtarı
  ona da uygulanır (`HikRender.SetPlaybackMotionBoxes`): SYSHEAD'de port açılınca, SDK portunu geç açarsa sonraki
  ~200 veri paketinde, `Started`'ta yeniden ve ayar değişince (karenin `_sdkGate` kuyruğunda, UI dışı). Her arama yeni
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
| `Views/DeviceEditor.xaml(.cs)` | Satır altı düzenleme paneli (eski DeviceDialog'un yerine): Bağlantı (iki eşit sütun; şifre/anahtar kutusu kayıtlı değeri göstermez, boş bırakılırsa değişmez), "Bağlantıyı test et", "Cihazdan oku", **HTTPS kullan** (ipucu: ayar/bilgi trafiğini şifreler, görüntü akışını etkilemez; yeni cihaz HTTPS + 443 ile açılır, kayıtlı cihaz kendi değerleriyle; işaretleyince 80→443, kaldırınca 443→80, özel port değişmez — Core `HttpsPort.Follow`; HTTPS açıkken cihaza TCP/TLS düzeyinde hiç ulaşılamazsa (`IsapiException.ConnectionFailed`, 401 vb. yanıtlar hariç) "HTTP ile dene / İptal" sorulur ([`ConfirmDialog`](src/SPVision/Views/ConfirmDialog.cs)), kabul edilirse forma HTTP (443 ise 80) yazılır ve bir kez yeniden denenir — sessiz düşürme yok), Kameralar tablosu (LİSTEDE GÖSTER anahtarı), alt satır Cihazı kaldır · Kameraları yenile · İptal · Kaydet. Taslakla çalışır; Kaydet ayarı yazar, giriş bilgisi değiştiyse SDK oturumunu kapatır ve cihazın karelerini yeniden bağlar |
| `Controls/RoundedClip.cs` | Yuvarlak köşeli kartın içindeki satır arka planlarını köşeden taşırmadan kırpar |
| `Views/LiveView.xaml(.cs)` | 1–36 kareli ızgara (düzen tanımı: Core `GridLayout`; kayıtlı 32 → 36), sayfalar (Core `LivePages`, `Sync`), sürükle-bırak/kare takası, düzeni + sayfayı kaydet/geri yükle; durumu `StateChanged`/`Message` ile ana pencereye bildirir |
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
(`PlaybackSlots`, `RecordingJump`, `PlaybackSpeed`, `PlaybackSync`, `CalendarMonth`, `PlaybackText` — oynatmanın saf mantığı, birim testli), [`LivePages`](src/SPVision.Core/LivePages.cs) (canlı sayfalar: global yuva sırası, sayfa sayısı/dilim/sınırlama/sarma, kare eşleme), [`CameraList`](src/SPVision.Core/CameraList.cs)
(yenilenen kamera listesinde anahtarı ve "listede göster" seçimini korur), [`DeviceKind`](src/SPVision.Core/DeviceKind.cs)
("NVR · 32 kanal" metni), [`AutoStart`](src/SPVision.Core/AutoStart.cs) (Run değeri) ve [`HttpsPort`](src/SPVision.Core/HttpsPort.cs) (HTTPS kutusu ↔ 80/443 portu, HTTP'ye düşme sorusunun kuralı).
Cihaz ayarları (§9): [`IsapiWritePolicy`](src/SPVision.Core/IsapiWritePolicy.cs) (yazma izin listesi + yasak kelimeler),
[`IsapiConfig.cs`](src/SPVision.Core/IsapiConfig.cs) (`IsapiPaths`, `IsapiResponseStatus`, `IsapiXml`, `ConfigEdit`/`SetValueEdit`,
`ConfigWrite`, `ConfigChangeText`, `IConfigTransport`, `ConfigWriter`), [`IsapiClient.Config.cs`](src/SPVision.Core/IsapiClient.Config.cs)
(`GetConfigAsync`, tek yazma yolu `PutConfigAsync`), [`RecorderSettings.cs`](src/SPVision.Core/RecorderSettings.cs) (ayrıştırıcılar,
`MotionSensitivity`, `RecordSchedule` hazır modları, `TimeSyncEdit`, `HikTimeZone`, `RecorderPlan` önce→sonra yazma planı). Yeni ayarlar:
`AppSettings.Theme` (metin olarak saklanır), `ShowMotionBoxes`, `RestoreLastView`, `StartFullScreen`; `Camera.Hidden`; `SavedView.Page`;
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
- **Kayıt cihazına yazma kuralı (21.10.2026'dan beri; eski "ISAPI'de yalnızca GET" kuralının yerine):**
  - **Akış şifreleme ve Hik-Connect asla değiştirilmez** (açık kalır) — sahibinin kesin kuralı. SPVision bunları yalnızca okur.
  - Güvenlikle ilgisiz ayarlar **yalnızca Ayarlar › Cihaz ayarları'nda, kullanıcı "Cihaza uygula" + onay** verdikten sonra yazılır;
    sayfa/sekme açılışında, form yüklenirken hiçbir şey yazılmaz.
  - Her yazma tek bir kapıdan geçer: `IsapiClient.PutConfigAsync` → `IsapiWritePolicy` (açık **izin listesi**; listede olmayan
    yol ağa çıkmadan reddedilir; encrypt / EZVIZ / Hik-Connect / platform / Security / Users / Network / factoryReset / reboot /
    format / upgrade / activate içeren yollar her durumda reddedilir — birim testli, Türkçe I sorunu yok: ordinal karşılaştırma).
  - Okuma-değiştirme-yazma (`ConfigWriter`): güncel XML GET edilir, yalnızca değişen öğeler değiştirilir, belge bütün olarak PUT
    edilir, `ResponseStatus` kontrol edilir, yeniden okunup doğrulanır; istekler tek tek gider, ilk hatada durulur. Ayrıntı §9.
  - 2. ve 3. aşama izin listesini genişletir (yeniden başlatma, biçimlendirme, ağ, kullanıcılar…); yasak kelime listesi kalır.
  - Başka hiçbir yerde ISAPI PUT/POST/DELETE ekleme (kayıt arama `POST ContentMgmt/search` salt okuma isteğidir, istisnadır).
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

**Sekmeler:** Genel bakış · Kameralar · Hareket · Kayıt · Sistem (1. aşama); Olaylar · Ağ · Kullanıcılar · Bakım görünür ama tek kart
gösterir: "Bu bölüm bir sonraki aşamada eklenecek." Her sekme ilk gösterildiğinde okunur (GET); sayfa her açılışta cihazı yeniden okur.
Okunamayan değer "okunamadı", cihazın sunmadığı ayar "Bu kamera desteklemiyor" yazar (403/404/405/501) — hata penceresi yok.

| Sekme | Okuma (GET) | Yazma (PUT, izin listesinde) |
|---|---|---|
| Genel bakış | `System/deviceInfo`, `ContentMgmt/InputProxy/channels` (+`/status`, `/capabilities` → toplam kanal `id@max`), `System/time`, `ContentMgmt/Storage/hdd`, `System/Network/EZVIZ` (yalnızca `enabled` + `streamEncrypteEnabled`; doğrulama kodu bu sayfada okunmaz) | — |
| Kameralar | `ContentMgmt/InputProxy/channels/{n}/video/overlays`, `Image/channels/{n}` (+`/capabilities`, `Color/*Level` min/max), `ContentMgmt/StreamingProxy/channels/{n}01` ve `{n}02` (+`/capabilities`) | kanal adı `ContentMgmt/InputProxy/channels/{n}` (`name`), OSD aynı overlays yolu (`DateTimeOverlay/enabled`, `channelNameOverlay/enabled`), görüntü `Image/channels/{n}` (`Color/brightnessLevel`, `contrastLevel`, `saturationLevel`), akış `StreamingProxy/channels/{n}0{1,2}` (`Video/videoResolutionWidth`+`Height` birlikte, `maxFrameRate` = fps×100, 0 = tam kare hızı; bit hızı VBR'de `vbrUpperCap`, CBR'de `constantBitRate`; `videoCodecType`) |
| Hareket | `ContentMgmt/InputProxy/channels/{n}/video/motionDetection` (+`/capabilities`: `sensitivityLevel` 0–100 adım 20) | aynı yol: `enabled`, `MotionDetectionLayout/sensitivityLevel` (Düşük 20 · Orta 60 · Yüksek 80; okurken 0–20 Düşük, 40–60 Orta, 80–100 Yüksek — `MotionSensitivity`) |
| Kayıt | `ContentMgmt/record/tracks/{n}01` | aynı yol: açık/kapalı = `CustomExtensionList/CustomExtension/enableSchedule`; mod = `TrackSchedule/ScheduleBlockList/ScheduleBlock` içindeki 7 tam gün `ScheduleAction` (`ActionRecordingMode` CMR = Sürekli, MOTION = Yalnızca hareket; eylem yok = Kapalı); `PreRecordTimeSeconds` / `PostRecordTimeSeconds`. Bunlara uymayan program **Özel program**dır, kullanıcı açıkça bir mod seçmedikçe yazılmaz |
| Sistem | `System/deviceInfo`, `System/time`, `System/time/ntpServers`, `ContentMgmt/Storage/hdd` | cihaz adı `System/deviceInfo` (`deviceName`); "Bilgisayarla eşitle" `System/time` (`timeMode`=manual, `localTime` = bu PC'nin saati + ofseti, `timeZone` farklıysa `CST-3:00:00` biçimi — işaret ters). NTP ve diskler yalnızca gösterilir; yeniden başlatma / biçimlendirme devre dışı ("Bir sonraki aşamada") |

**Kanal numaraları:** ISAPI kanal no 1–22 (InputProxy), akış/kayıt izi `kanal×100 + 1/2` (101, 102 … 2201). SDK'daki 33+ numaraları burada **kullanılmaz**.

**Yazma akışı:** form değeri ↔ son okunan değer farkı `RecorderPlan` ile `ConfigWrite` listesine çevrilir → "Cihaza uygula" her değişikliği
"<kamera> · <alan>: <eski> → <yeni>" satırıyla listeleyen onay ister (`ConfirmDialog` liste aşırı yüklemesi) → `ConfigWriter` her belgeyi
sırayla GET → yalnızca değişen öğeleri değiştir (XDocument, ad alanı ve diğer öğeler korunur) → PUT → `ResponseStatus` (`statusCode` 1 = OK;
diğerleri Türkçe açıklamayla hata, ör. "Geçersiz işlem (notSupport: cihaz bu ayarı desteklemiyor)") → yeniden GET + doğrula. İlk hatada durur;
kartta "Uygulandı" ya da "Cihaz değeri kabul etmedi: …" yazar, hata varsa UYGULANDI / UYGULANMADI listesi gösterilir; ardından sekme cihazdan
yeniden okunur. "Geri al" formu son okunan değerlere döndürür (cihaza bir şey göndermez). Gövdede `password`/`encrypt`/`EZVIZ`/`platform`/
`verificationCode` adlı öğe varsa PUT reddedilir.

**Dikkat:** kanal adı yazımı `InputProxy/channels/{n}` belgesini bütün olarak geri yazar (kameranın IP/port/kullanıcı adı dahil, şifre öğesi
GET'te gelmez ve gönderilmez). Bu cihazda denenmedi; ad değişikliğinden sonra kameranın çevrimiçi kaldığını doğrulayın.
