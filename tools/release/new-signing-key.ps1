<#
.SYNOPSIS
  SPVision güncelleme imza anahtarını (ECDSA P-256) BİR KEZ oluşturur.

.DESCRIPTION
  - Gizli anahtarı (PKCS#8 PEM) seçtiğiniz dosyaya yazar (varsayılan: %USERPROFILE%\SPVision-guncelleme-imza-anahtari.pem)
    ve dosyayı yalnız sizin okuyabileceğiniz şekilde kilitler.
  - Açık anahtarı uygulamanın kaynağına yazar: src\SPVision.Core\Updates\UpdateTrust.cs (BEGIN/END işaretleri arası).
    Bu değişikliği commit edin; açık anahtar gizli değildir.
  - -SetGitHubSecret verilirse gizli anahtarı GitHub secret UPDATE_SIGNING_KEY olarak da kaydeder (yalnız CI ile
    yayınlayacaksanız gerekir; yerel yayında gerekmez).

  Anahtar DEĞİŞTİRMEK dikkat ister: eski açık anahtarı taşıyan SPVision'lar yeni anahtarla imzalanmış sürümü reddeder.
  Bu yüzden betik, uygulamada zaten gerçek bir anahtar varsa -Force olmadan durur; var olan bir anahtar dosyasının üzerine
  ise HİÇ yazmaz (eski anahtar, değişim sürümünü imzalamak için gerekir — yeni anahtar için başka bir -OutFile seçin).
  Anahtar değişimi adımları: CLAUDE_REHBERI.md › "Uygulama güncelleme" › "Anahtar değiştirme".

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release\new-signing-key.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release\new-signing-key.ps1 -OutFile D:\guvenli\spvision.pem -SetGitHubSecret
#>
[CmdletBinding()]
param(
    [string]$OutFile = (Join-Path $env:USERPROFILE 'SPVision-guncelleme-imza-anahtari.pem'),
    [switch]$SetGitHubSecret,
    [switch]$Force,
    [string]$Repo = 'SimonPetrus/SPVision'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $root 'src\SPVision.Core\Updates\UpdateTrust.cs'
$signer = Join-Path $root 'tools\release\UpdateSigner\UpdateSigner.csproj'

function Fail([string]$Message) {
    Write-Host "HATA: $Message" -ForegroundColor Red
    exit 1
}

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
if ($OutFile.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "Gizli anahtar depo klasörünün içine yazılmaz (yanlışlıkla commit edilebilir). Depo dışında bir yol seçin."
}
if (Test-Path $OutFile) {
    Fail "$OutFile zaten var; üzerine yazılmaz. Var olan anahtarı kullanın. Anahtar değiştiriyorsanız yeni anahtar için başka bir -OutFile seçin ve eskisini SAKLAYIN."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "dotnet bulunamadı (.NET 10 SDK gerekli)."
}

Write-Host ""
Write-Host "==============================================================================" -ForegroundColor Yellow
Write-Host " UYARI: Oluşturulacak GİZLİ ANAHTAR, SPVision güncellemelerini imzalamanın TEK yoludur." -ForegroundColor Yellow
Write-Host "  - Kimseyle paylaşmayın, e-postayla göndermeyin, depoya koymayın." -ForegroundColor Yellow
Write-Host "  - Yedekleyin (parola yöneticisi / şifreli USB). Kaybolursa kurulu SPVision'lar bir daha" -ForegroundColor Yellow
Write-Host "    otomatik güncellenemez; her bilgisayara elle yeni kurulum gerekir." -ForegroundColor Yellow
Write-Host "  - Ele geçirilirse, o kişi sahte güncelleme imzalayabilir: hemen anahtar değiştirin." -ForegroundColor Yellow
Write-Host "==============================================================================" -ForegroundColor Yellow
Write-Host ""

$arguments = @('run', '--project', $signer, '-c', 'Release', '--', 'keygen', '--private-out', $OutFile, '--source', $source)
if ($Force) {
    $arguments += '--force'
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    Fail "Anahtar oluşturulamadı."
}

# Only the current user may read the private key file.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $OutFile /inheritance:r /grant:r "*${sid}:(R,W)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Uyarı: dosya izinleri kısıtlanamadı; $OutFile dosyasını korunan bir yere taşıyın." -ForegroundColor Yellow
}

if ($SetGitHubSecret) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail "gh (GitHub CLI) bulunamadı; secret'ı elle ekleyin: GitHub › Settings › Secrets and variables › Actions › UPDATE_SIGNING_KEY."
    }
    # The file goes to gh byte for byte: piping a string through Windows PowerShell 5.1 re-encodes it, and the stored
    # secret could not be read as PEM in CI (22.10.2026).
    $gh = Start-Process gh -ArgumentList @('secret', 'set', 'UPDATE_SIGNING_KEY', '--repo', $Repo) -RedirectStandardInput $OutFile `
        -NoNewWindow -Wait -PassThru
    $global:LASTEXITCODE = $gh.ExitCode
    if ($LASTEXITCODE -ne 0) {
        Fail "GitHub secret kaydedilemedi."
    }
    Write-Host "GitHub secret UPDATE_SIGNING_KEY kaydedildi ($Repo)." -ForegroundColor Green
}

Write-Host ""
Write-Host "Tamam. Sıradaki adımlar:" -ForegroundColor Green
Write-Host "  1. Gizli anahtarı YEDEKLEYİN: $OutFile"
Write-Host "  2. Açık anahtar değişikliğini commit edin: git add src\SPVision.Core\Updates\UpdateTrust.cs; git commit -m `"Güncelleme imza anahtarı`""
Write-Host "  3. Bu açık anahtarı taşıyan sürüm kurulduktan SONRA yayınlanan sürümler uygulama içinden güncellenir."
