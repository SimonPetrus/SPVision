# SPVision

Hikvision kayıt cihazları (NVR/DVR) ve kameralar için hafif bir izleme istemcisi. Eski **iVMS-4200 Lite**'ın
yerine, onu tersine mühendislikle inceledikten sonra sıfırdan yazıldı (bkz. [reversing/BULGULAR.md](reversing/BULGULAR.md)).

> Ayrıntılı rehber (mimari, dosya haritası, doğrulanmış durum, sıradaki iş): [CLAUDE_REHBERI.md](CLAUDE_REHBERI.md)

## Neden?

iVMS-4200 Lite 4 kameralık bir ızgarada bir işlemci çekirdeğini tamamen meşgul ediyordu: tam çözünürlüklü H.265
akışlarını 32-bit eski bir decoder ile **yazılımsal** çözüyor ve arka planda bulut/güncelleme servisleri çalıştırıyordu.

SPVision:

- Görüntüyü **ekran kartında** çözer ve çizer.
- Video **iki yoldan** akar:
  - **Şifreli akışlar** (akış şifreleme açık): Hikvision'ın resmi **Device Network SDK**'sı
    (HCNetSDK + PlayCtrl), donanım (GPU) decode ile.
  - **Şifresiz akışlar:** [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf) + FFmpeg 9, Direct3D 11
    (NVIDIA/Intel/AMD) üzerinden RTSP.
  - Kamera için akış anahtarı varsa SDK yolu seçilir; FFmpeg akışı açamazsa uygulama bir kez SDK yoluna düşer.
- Cihaz bilgisi, kamera listesi, kayıt arama ve anlık görüntü için standart **ISAPI (HTTP)** kullanır;
  ISAPI'de yalnızca okuma (GET) yapılır, **cihaz ayarları hiçbir zaman değiştirilmez**.
- Küçük karelerde alt akışı (SD), büyük karelerde ana akışı (HD) otomatik seçer; elle HD/SD de seçilebilir.
- Tek, 64-bit süreç. Bulut, push ya da güncelleme servisi yoktur.
- Cihaz şifresi ve akış anahtarı bir kez girilir ve Windows DPAPI ile şifreli saklanır.

> **Not — akış şifreleme:** Hikvision'ın "Akış Şifreleme" (stream encryption) özelliği açıkken görüntü
> standart değildir ve FFmpeg tarafından çözülemez. Bu akışlar **SDK yolu** ile, akış anahtarı (doğrulama kodu)
> kullanılarak oynatılır. Anahtar cihaz ekleme/düzenleme ekranındaki **"Cihazdan oku"** düğmesiyle ISAPI
> üzerinden cihazdan okunur ya da bir kez elle girilir; ardından DPAPI ile şifreli saklanır. Kayıt cihazında
> şifreleme ya da başka bir güvenlik ayarı **kapatılmaz**.

## Özellikler (v0.1)

- Canlı izleme: 6/8/25/32 ızgara (6 ve 8: bir büyük kare + küçükler), sürükle-bırak (kareler arası takas dahil), çift tıkla büyüt, tam ekran (F11), HD/SD/Otomatik kalite
- Şifreli ve şifresiz akışlar için çift video yolu (SDK / FlyleafLib), kopmada otomatik yeniden bağlanma (3/10/30 sn)
- Kayıt oynatma: güne göre NVR kayıt arama, zaman çizelgesi (tekerlekle yakınlaştırma), oynat/duraklat, ±30 sn atlama
- Anlık görüntü (NVR'dan tam çözünürlüklü JPEG) ve yerel MP4 kayıt/klip
- Cihaz ekleme/düzenleme; akış anahtarını cihazdan okuma
- Son görünüm ve kalite seçimi açılışta geri yüklenir

## Kurulum (geliştirici)

Gereksinimler: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download).

1. **Hikvision SDK'sını el ile yerleştirin.** SDK lisanslıdır ve repoda yoktur. Hikvision'dan edinilen
   *Device Network SDK V6.1.11.40 (win64)* paketini şu klasöre açın:
   `sdk/EN-HCNetSDKV6.1.11.40_build20260922_win64/` (DLL'ler `lib/` altında olmalı).
   Derleme, SDK DLL'lerini (`HCNetSDKCom\` alt klasörüyle birlikte) çıktı klasörüne otomatik kopyalar.
   SDK yoksa derleme yalnızca uyarı verir; şifreli akışlar o zaman açılamaz.
2. FFmpeg DLL'lerini indirin, derleyin ve çalıştırın:

```powershell
.\tools\get-ffmpeg.ps1          # FFmpeg 9 LGPL paylaşımlı DLL'lerini ffmpeg\ klasörüne indirir
dotnet build SPVision.slnx
dotnet test tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj
dotnet run --project src\SPVision
```

Ayarlar ve loglar: `%APPDATA%\SPVision\` · Görüntüler: `Resimler\SPVision` · Kayıtlar: `Videolar\SPVision`
## Proje yapısı

| Klasör | İçerik |
|---|---|
| `src/SPVision.Core` | Modeller, ISAPI istemcisi, RTSP adresleri, şifreli ayar deposu |
| `src/SPVision` | WPF arayüzü (canlı izleme, oynatma, cihaz yönetimi) |
| `src/SPVision/Hik/` | Hikvision SDK katmanı: P/Invoke imzaları, SDK oturumu, canlı ve oynatma SDK oynatıcıları |
| `tests/SPVision.Core.Tests` | Birim testleri |
| `sdk/` | Hikvision Device Network SDK (lisanslı, **repoda yok**, el ile yerleştirilir; `.gitignore`'da) |
| `reversing/` | iVMS-4200 Lite analiz notları ve Ghidra script'i (ham decompile çıktısı repoda yok) |
| `tools/` | FFmpeg indirici, Qt `.qm` çözücü, PlayCtrl export denetleyici |

## Lisanslar

Şifresiz video yolu [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf) (LGPL-3.0) ve FFmpeg (LGPL) kullanır.
Şifreli video yolu Hikvision Device Network SDK'sını (HCNetSDK + PlayCtrl) kullanır; SDK, üreticinin kendi
lisansına tabidir ve bu repoda **dağıtılmaz**.
