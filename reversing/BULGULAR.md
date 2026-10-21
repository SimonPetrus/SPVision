# iVMS-4200 Lite — Tersine Mühendislik Bulguları

Hedef: `C:\Program Files\iVMS-4200 Lite\iVMS-4200 Lite.exe` (v2.00.00.00, 2019, 32-bit, Qt5 + Hikvision HCNetSDK).
Araçlar: Ghidra 12.1.4 headless (`ghidra_scripts/ExportAll.java`), Qt `.qm` çözücü (`../tools/qm_dump.py`),
çalışan süreç ölçümleri ve uygulama logları.

> Ham decompile çıktıları `raw/` altındadır ve Hikvision'a ait kod içerdiği için repoya **gönderilmez** (`.gitignore`).
> Yeni uygulama bu koddan kopya içermez; yalnızca davranış ve protokol bilgisi kullanılır.

## 1. Mimari

| Katman | Dosya | Görev |
|---|---|---|
| Kabuk | `iVMS-4200 Lite.exe` | Qt5 ana pencere, ayarlar, `Plugin.xml`'deki eklentileri yükler, SDK'yı başlatır |
| Canlı izleme UI | `MainViewPlugin.dll` | Izgara, kamera ağacı, döngü (cycle), görünüm kaydetme |
| Canlı izleme mantığı | `PreviewLogical.dll` | `NET_DVR_RealPlay_V40` + `SetRealDataCallBack`, PTZ, akış tipi |
| Oynatma | `PlaybackPlugin.dll`, `PlaybackLogical.dll` | `FindFile_V50`, `PlayBackByTime_V40`, `PlayBackControl_V40`, etiketler |
| Cihaz yönetimi | `DevicePlugin.dll`, `DeviceAccessLayer.dll` | Giriş, cihaz/kanal bilgisi, Ezviz bulut |
| Decode | `DecodeProcess\DecodeProcess.exe` | Ayrı süreç; ana uygulamadan XML mesajlarla akış alır, `PlayCtrl.dll` ile çözer ve pencereye (HWND) çizer |
| Diğer | `UpgradeServerPlugin.dll`, `PushClient.dll`, `paho-mqtt3c.dll`, `EzvizOpenSDKComm.dll` | Cihaz yükseltme sunucusu (WebSocket :15200), bulut push, MQTT, Ezviz |

Veri dosyaları (`Device`, `Entity`, `Local`, `MainView`, `System`) SQLite'tır; canlı olanlar şifrelidir.
Boş şablonları `maintain\*.benchmark` altında bulunur. Önemli tablolar:

- `Device(GUID, Name, LoginAddress, LoginPort, UserName, UserPwd, SN, AChanCount, DChanCount, …)`
- `Channel(GUID, DeviceGUID, Name, ChannelType, ChannelNo, Enable)`
- `Camera(ID, GroupID, Name, StreamType, ChannelGUID, PlaybackStreamType, …)`
- `ViewInfo(ID, Name, SplitType, CameraIDs)` — kayıtlı ızgara görünümleri
- `System(… EnableHardDecode DEFAULT 0, EnableAutoChangeStreamType, VideoSavedPath, PictureSavedPath …)`

## 2. Cihazla iletişim (DeviceAccessLayer)

Yalnızca 6 HCNetSDK fonksiyonu kullanılıyor:

| SDK çağrısı | Amaç | Açık standart karşılığı (ISAPI) |
|---|---|---|
| `NET_DVR_Login_V40` / `Logout_V30` | Oturum (TCP 8000, kapalı protokol) | HTTP Digest auth, oturumsuz |
| `GetDVRConfig(DEVICECFG_V40)` | Cihaz adı, seri no, kanal sayıları | `GET /ISAPI/System/deviceInfo` |
| `GetDVRConfig(IPPARACFG_V40)` | NVR'a bağlı IP kameralar | `GET /ISAPI/ContentMgmt/InputProxy/channels` (+ `/status`) |
| `GetDVRConfig(PICCFG_V40)` | Kanal adı (OSD) | `GET /ISAPI/System/Video/inputs/channels` |

`dal::ChannelHelper::GetChannelName` her kanal için 0x12D54 (~77 KB) boyutlu bir yapı istiyor; loglarda
bu çağrı **her 3 dakikada 10 kanal için** tekrar ediliyor ve sürekli başarısız oluyor (`error code: 0`).

## 3. Canlı izleme hattı ve CPU yükünün kaynağı

```
NVR :8000 ──(Hik özel protokol, kanal başına 1 TCP)──► iVMS-4200 Lite.exe (RealPlay_V40 callback)
      ──(XML IPC: start_play, head_data, hwnd)──► DecodeProcess.exe ──PlayCtrl (yazılım decode)──► HWND
```

Ölçüm (20.10.2026, 2×2 ızgara, 4 kamera açık):

| Süreç | CPU (1 çekirdek = %100) | Bellek | İş parçacığı |
|---|---|---|---|
| DecodeProcess.exe | **~%107** | 351 MB | 30 |
| iVMS-4200 Lite.exe | ~%6.5 | 405 MB | 209 |

GPU video decoder kullanımı: **%0**.

`start_play` mesajındaki 40 baytlık `head_data` (`IMKH` Hikvision medya başlığı) çözüldü:

| Alan | Değer | Anlam |
|---|---|---|
| sistem formatı | `0x0002` | MPEG-PS |
| video codec | `0x0005` | **H.265** |
| ses codec | `0x7110` | G.711 A-law, 8 kHz, mono |

`stream_type=0` (ana akış = tam çözünürlük) ve `idecodetype=0` (yazılım decode).
**Sonuç:** 4 adet tam çözünürlüklü H.265 akışı, 32-bit eski PlayCtrl ile işlemcide çözülüyor.
Ayarlardaki `EnableHardDecode` varsayılan olarak kapalı; küçük karelerde alt akışa geçiş de yok.

## 4. Oynatma (PlaybackLogical)

`NET_DVR_FindFile_V50` / `FindNextFile_V50` ile kayıt arama, `PlayBackByTime_V40` ile zamana göre oynatma,
`PlayBackControl_V40` ile hız/duraklatma, `PlayBackReverseByTime_V40` ile geri oynatma; etiket (label) desteği.
ISAPI/RTSP karşılıkları:

- Kayıt arama: `POST /ISAPI/ContentMgmt/search` (`CMSearchDescription`)
- Oynatma: `rtsp://<nvr>:554/Streaming/tracks/<kanal>01?starttime=YYYYMMDDThhmmssZ&endtime=…`
- İndirme: `GET /ISAPI/ContentMgmt/download` (`downloadRequest` + `playbackURI`)

## 5. Cihaz (ağ taraması)

`192.168.1.2` — NVR, ~10 kanal. Açık portlar: 80 (ISAPI, Digest MD5), 443, **554 (RTSP)**, 8000 (SDK).
Kullanılan kameralar (log): DIS KAPI, ATOLYE 3, HESAP MASA, CILA 2.

## 6. Gereksiz arka plan işleri

- Ezviz bulut SDK'sı ve gömülü AppKey/AppSecret değerleri (`Setup.xml`)
- `UpgradeServerPlugin` → yerel WebSocket sunucusu :15200, cihaz ekleme bildirimleri
- `PushClient`, MQTT, log4cxx ile 5 MB'lık `ClientLog.txt`
- 209 iş parçacığı

## 7. Yeni uygulama için karar

- C# / .NET 10 / WPF, video: FFmpeg 9 + Direct3D 11 (FlyleafLib) → **GPU'da decode ve çizim, tek 64-bit süreç**
- Cihaz erişimi: yalnızca ISAPI (HTTP Digest) + RTSP; Hikvision DLL'i yok
- Küçük karelerde alt akış (`/Streaming/Channels/<n>02`), büyütülen/tam ekran karede ana akış (`<n>01`)
- Kanal bilgileri açılışta ve elle yenilemede alınır; periyodik sorgu yok
- Şifreler Windows DPAPI ile şifrelenerek saklanır
