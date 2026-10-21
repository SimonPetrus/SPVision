# SPVision güncelleme vekili (Cloudflare Worker)

GitHub deposu **özel** kalır. Bilgisayarlardaki SPVision yalnız bu küçük Worker'a bağlanır; Worker en son GitHub
sürümünün dosyalarını **kendi belirteciyle** okuyup verir:

| Adres | Ne verir |
|---|---|
| `GET /manifest.json` | En son sürümdeki `manifest.json` (sürüm, zorunlu mu, notlar, kurulum dosyası adı, SHA-256, boyut) |
| `GET /manifest.json.sig` | Onun imzası (ECDSA P-256) |
| `GET /download` | Kurulum dosyası: GitHub'ın birkaç dakika geçerli imzalı indirme adresine yönlendirme (302) |
| başka her şey | 404 |

"En son sürüm" = GitHub'daki en yeni **yayınlanmış** sürüm (taslak ve "pre-release" sayılmaz). Sürüm bilgisi ~60 sn
önbelleklenir. Güvenlik uygulamadadır: uygulama imzayı kendi içindeki açık anahtarla, indirdiği dosyayı imzalı
SHA-256 ile doğrular. Worker ele geçirilse bile sahte bir program kurdurulamaz.

## Bir kerelik kurulum

Gerekenler: ücretsiz bir Cloudflare hesabı (https://dash.cloudflare.com/sign-up) ve Node.js (bilgisayarda kurulu).
Komutlar PowerShell'de, depo klasöründen çalıştırılır.

### 1. GitHub belirteci (ince ayarlı PAT) oluşturun

1. GitHub → sağ üstte profil resmi → **Settings** → en altta **Developer settings** → **Personal access tokens** →
   **Fine-grained tokens** → **Generate new token**.
2. Ayarlar — **yalnız bunlar**:
   - **Token name:** `spvision-update-proxy`
   - **Expiration:** 1 yıl (takvime "belirteci yenile" hatırlatması koyun; süresi dolunca güncelleme denetimi
     "Güncelleme sunucusu yanıt vermedi (HTTP 502)" der)
   - **Resource owner:** `SimonPetrus`
   - **Repository access:** **Only select repositories** → `SimonPetrus/SPVision`
   - **Permissions → Repository permissions → Contents:** **Read-only**
     (Metadata: Read-only kendiliğinden gelir; başka hiçbir izin vermeyin)
3. **Generate token** → belirteci kopyalayın (bir daha gösterilmez). Hiçbir dosyaya yazmayın; 4. adımda yapıştıracaksınız.

### 2. Cloudflare'e giriş

```powershell
cd tools\update-proxy
npx wrangler login
```

Tarayıcı açılır, Cloudflare hesabınızla izin verin.

### 3. Worker'ı yayınlayın

```powershell
npx wrangler deploy
```

İlk seferde `workers.dev` alt alan adı seçmeniz istenebilir. Çıktının sonunda adres yazar, örneğin
`https://spvision-update.ornek.workers.dev` — bunu not edin.

### 4. Belirteci Worker'a gizli olarak verin

```powershell
npx wrangler secret put GITHUB_TOKEN
```

Sorulunca 1. adımdaki belirteci yapıştırıp Enter'a basın (ekranda görünmez). Belirteç yalnız Cloudflare'de şifreli
saklanır; depoda, `wrangler.toml`'da ya da uygulamada **yoktur**.

### 5. Deneyin

```powershell
curl.exe -i https://spvision-update.ornek.workers.dev/manifest.json
```

- Henüz sürüm yayınlanmadıysa: `404 Bulunamadı` (doğru).
- `503 Yapılandırılmamış`: 4. adım yapılmamış.
- `502`: belirteç yanlış / süresi dolmuş / izni eksik.

### 6. Adresi uygulamaya yazın

`src\SPVision\Updates\UpdateService.cs` → `UpdateProxyBaseUrl` sabitini 3. adımdaki adresle değiştirin
(sonunda `/` olmadan). Yer tutucu (`<hesap>`) kaldıkça uygulama "Güncelleme sunucusu ayarlanmamış" der ve hiçbir
şey denetlemez.

## Bakım

- **Belirteci yenileme:** GitHub'da yeni belirteç (aynı ayarlar) → `npx wrangler secret put GITHUB_TOKEN` → eskisini
  GitHub'da silin.
- **Günlük:** `npx wrangler tail` (istekleri canlı gösterir; belirteç hiçbir yanıtta ya da günlükte yer almaz).
- **Kod değişince:** `npx wrangler deploy`.
- **Testler** (Cloudflare gerekmez): `npm test`.
- İsteğe bağlı: Cloudflare panelinde bu Worker için bir hız sınırı kuralı (ör. IP başına dakikada 30 istek) eklenebilir.

## Notlar

- Önbellek: Worker sürüm bilgisini isolate belleğinde 60 sn tutar; özel alan adında Cache API de kullanılır
  (`*.workers.dev` üzerinde Cache API etkisizdir, bellek önbelleği yine çalışır). GitHub API sınırı (saatte 5000 istek)
  bu yüzden sorun olmaz.
- `/download` her zaman **manifest'te adı geçen** dosyayı verir; dosya adı `^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$` olmalıdır.
- CORS yok (tarayıcıdan kullanılmaz). Yalnız `GET`.
