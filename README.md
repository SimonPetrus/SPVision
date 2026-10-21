# SP-2026 Lite

Hikvision kayıt cihazları (NVR/DVR) ve kameralar için hafif bir izleme istemcisi. Eski **iVMS-4200 Lite**'ın
yerine, onu tersine mühendislikle inceledikten sonra sıfırdan yazıldı (bkz. [reversing/BULGULAR.md](reversing/BULGULAR.md)).

## Neden?

iVMS-4200 Lite 4 kameralık bir ızgarada bir işlemci çekirdeğini tamamen meşgul ediyordu: tam çözünürlüklü H.265
akışlarını 32-bit eski bir decoder ile **yazılımsal** çözüyor ve arka planda bulut/güncelleme servisleri çalıştırıyordu.

SP-2026 Lite:

- Görüntüyü **ekran kartında** çözer ve çizer (FFmpeg 9 + Direct3D 11, NVIDIA/Intel/AMD).
- Küçük karelerde alt akışı (SD), büyük karelerde ana akışı (HD) otomatik seçer; elle HD/SD de seçilebilir.
- Hikvision SDK'sı kullanmaz; yalnızca standart **ISAPI (HTTP)** ve **RTSP** ile konuşur.
- Tek, 64-bit süreç. Bulut, push ya da güncelleme servisi yoktur.
- Cihaz şifresi bir kez girilir ve Windows DPAPI ile şifreli saklanır.

> **Not:** Hikvision'ın "Akış Şifreleme" (stream encryption) özelliği açıkken görüntü standart değildir ve
> FFmpeg tarafından çözülemez. Bu durumda kayıt cihazının web arayüzünden kapatın:
> *Yapılandırma › Ağ › Gelişmiş Ayarlar › Platform Erişimi (Hik-Connect) › Akış Şifreleme*.

## Özellikler (v0.1)

- Canlı izleme: 1/4/9/16 ızgara, sürükle-bırak, çift tıkla büyüt, tam ekran (F11), HD/SD/Otomatik kalite
- Kayıt oynatma: güne göre NVR kayıt arama, zaman çizelgesi (tekerlekle yakınlaştırma), ±30 sn atlama
- Anlık görüntü (NVR'dan tam çözünürlüklü JPEG) ve yerel MP4 kayıt/klip
- Son görünüm ve kalite seçimi açılışta geri yüklenir

## Kurulum (geliştirici)

Gereksinimler: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
.\tools\get-ffmpeg.ps1          # FFmpeg 9 LGPL paylaşımlı DLL'lerini ffmpeg\ klasörüne indirir
dotnet build SP2026Lite.slnx
dotnet test
dotnet run --project src\SP2026Lite
```

Ayarlar: `%APPDATA%\SP-2026 Lite\settings.json` · Görüntüler: `Resimler\SP-2026 Lite` · Kayıtlar: `Videolar\SP-2026 Lite`

## Proje yapısı

| Klasör | İçerik |
|---|---|
| `src/SP2026Lite.Core` | Modeller, ISAPI istemcisi, RTSP adresleri, şifreli ayar deposu |
| `src/SP2026Lite` | WPF arayüzü (canlı izleme, oynatma, cihaz yönetimi) |
| `tests/SP2026Lite.Core.Tests` | Birim testleri |
| `reversing/` | iVMS-4200 Lite analiz notları ve Ghidra script'i (ham decompile çıktısı repoda yok) |
| `tools/` | FFmpeg indirici, Qt `.qm` çözücü |

## Lisanslar

Video katmanı [FlyleafLib](https://github.com/SuRGeoNix/Flyleaf) (LGPL-3.0) ve FFmpeg (LGPL) kullanır.
