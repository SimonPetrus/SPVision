<#
.SYNOPSIS
  SPVision sürümünü YEREL olarak derleyip GitHub'a yayınlar (önerilen yol).

.DESCRIPTION
  Sırayla:
    1. Sürüm: src\SPVision\SPVision.csproj <Version> (ör. 1.2.0) → etiket v1.2.0; release-notes\v1.2.0.md olmalı.
    2. Denetimler: imza anahtarı dosyası, uygulamadaki açık anahtar ve vekil adresi (yer tutucu değil), çalışma ağacı temiz,
       commit GitHub'a gönderilmiş, etiket / sürüm daha önce yok, sdk\ ve ffmpeg\ DLL'leri yerinde, Inno Setup kurulu.
    3. Testler (dotnet test).
    4. dotnet publish (Release, win-x64, self-contained) → artifacts\release\<sürüm>\publish; SDK + FFmpeg DLL'leri denetlenir.
    5. Inno Setup → artifacts\release\<sürüm>\SPVision-Kurulum-<sürüm>.exe
    6. manifest.json (SHA-256 + boyut + notlar) → imza (manifest.json.sig) → uygulamanın açık anahtarıyla doğrulama.
    7. Onaydan sonra: gh release create v<sürüm> (kurulum + manifest.json + manifest.json.sig).

  Lisanslı Hikvision SDK'sı ve FFmpeg yalnız bu bilgisayardaki sdk\ ve ffmpeg\ klasörlerinden gelir; internete yalnız
  kurulum paketinin içinde (özel GitHub sürümü) çıkar.

.PARAMETER SigningKey
  Gizli imza anahtarı (new-signing-key.ps1'in yazdığı PEM). Varsayılan: %USERPROFILE%\SPVision-guncelleme-imza-anahtari.pem

.PARAMETER KeyRotation
  Anahtar değişimi sürümü: uygulamada YENİ açık anahtar var, -SigningKey ise ESKİ gizli anahtardır (kurulu kopyalar yalnız
  onu tanır). Yalnız bu tek sürüm için; sonrakiler yeni anahtarla, bayraksız yayınlanır.

.PARAMETER DryRun
  Her şeyi yapar, GitHub'a hiçbir şey yüklemez (çıktı artifacts\release\<sürüm>\ altında kalır). Temiz olmayan çalışma ağacına
  ve gönderilmemiş commit'e izin verir (yalnız uyarır).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release\release.ps1 -DryRun
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release\release.ps1
#>
[CmdletBinding()]
param(
    [string]$SigningKey = (Join-Path $env:USERPROFILE 'SPVision-guncelleme-imza-anahtari.pem'),
    [switch]$DryRun,
    [switch]$KeyRotation,
    [switch]$SkipTests,
    [switch]$Yes,
    [string]$Repo = 'SimonPetrus/SPVision'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $root
try {
    function Fail([string]$Message) {
        Write-Host "HATA: $Message" -ForegroundColor Red
        exit 1
    }
    function Step([string]$Message) {
        Write-Host ""
        Write-Host "== $Message" -ForegroundColor Cyan
    }
    function Warn([string]$Message) {
        Write-Host "Uyarı: $Message" -ForegroundColor Yellow
    }
    # Runs a native command; stops the release when it fails.
    function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) {
            Fail "$Exe $($Arguments[0]) başarısız (çıkış kodu $LASTEXITCODE)."
        }
    }
    # Runs a native command quietly; true when it succeeded (stderr of a failing gh/git call is not an error here).
    function Test-Native([scriptblock]$Block) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        try {
            $null = & $Block 2>&1
            return ($LASTEXITCODE -eq 0)
        }
        finally {
            $ErrorActionPreference = $previous
        }
    }

    # Output of a native command whose stderr is not an error here (empty on failure).
    function Get-Native([scriptblock]$Block) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        try {
            return @(& $Block 2>$null)
        }
        finally {
            $ErrorActionPreference = $previous
        }
    }

    # ---- 1. Version ----
    Step "Sürüm"
    [xml]$project = Get-Content -Raw -Encoding UTF8 'src\SPVision\SPVision.csproj'
    $version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        Fail "SPVision.csproj <Version> '$version' 1.2.3 biçiminde değil."
    }
    $tag = "v$version"
    $notes = Join-Path $root "release-notes\$tag.md"
    if (-not (Test-Path $notes)) {
        Fail "Sürüm notları yok: release-notes\$tag.md (biçim: CLAUDE_REHBERI.md › Uygulama güncelleme)."
    }
    Write-Host "SPVision $version (etiket $tag)"

    # ---- 2. Checks ----
    Step "Denetimler"
    if (-not (Test-Path $SigningKey)) {
        Fail "İmza anahtarı bulunamadı: $SigningKey (önce tools\release\new-signing-key.ps1, ya da -SigningKey <yol>)."
    }
    $trust = Get-Content -Raw -Encoding UTF8 'src\SPVision.Core\Updates\UpdateTrust.cs'
    if ($trust -notmatch '-----BEGIN PUBLIC KEY-----') {
        Fail "Uygulamada açık anahtar yok (yer tutucu). Önce tools\release\new-signing-key.ps1 çalıştırıp UpdateTrust.cs'i commit edin."
    }
    $service = Get-Content -Raw -Encoding UTF8 'src\SPVision\Updates\UpdateService.cs'
    if ($service -match 'UpdateProxyBaseUrl = "[^"]*<') {
        $message = "UpdateService.UpdateProxyBaseUrl hâlâ yer tutucu: bu sürümü kuranlar sonraki sürümleri GÖREMEZ."
        if ($DryRun) { Warn $message } else { Fail "$message Önce Worker adresini yazın (tools\update-proxy\README.md)." }
    }
    foreach ($tool in @('dotnet', 'git')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool bulunamadı." }
    }
    if (-not $DryRun) {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "gh (GitHub CLI) bulunamadı." }
        if (-not (Test-Native { gh auth status })) { Fail "gh oturumu yok: gh auth login" }
    }
    $iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        $command = Get-Command iscc -ErrorAction SilentlyContinue
        if ($command) { $iscc = $command.Source }
    }
    if (-not $iscc) {
        Fail "Inno Setup 6 bulunamadı (https://jrsoftware.org/isdl.php ya da: winget install JRSoftware.InnoSetup)."
    }

    $dirty = Get-Native { git status --porcelain }
    if ($dirty) {
        if ($DryRun) { Warn "Çalışma ağacında commit edilmemiş değişiklik var." } else { Fail "Çalışma ağacında commit edilmemiş değişiklik var; önce commit edin." }
    }
    $commit = (& git rev-parse HEAD).Trim()
    $null = Test-Native { git fetch origin --quiet }
    $remoteBranches = Get-Native { git branch -r --contains $commit }
    if (-not $remoteBranches) {
        if ($DryRun) { Warn "Commit $commit GitHub'da yok." } else { Fail "Commit $commit GitHub'a gönderilmemiş: git push" }
    }
    if (Test-Native { git rev-parse -q --verify "refs/tags/$tag" }) {
        Fail "$tag etiketi zaten var. Yeni sürüm için SPVision.csproj <Version> değerini artırın."
    }
    if (-not $DryRun) {
        if (Test-Native { gh release view $tag --repo $Repo }) {
            Fail "GitHub'da $tag sürümü zaten var."
        }
        $remoteTag = Get-Native { git ls-remote --tags origin "refs/tags/$tag" }
        if ($remoteTag) { Fail "GitHub'da $tag etiketi zaten var." }
    }

    $sdkLib = Join-Path $root 'sdk\EN-HCNetSDKV6.1.11.40_build20260922_win64\lib'
    if (-not (Test-Path (Join-Path $sdkLib 'HCNetSDK.dll'))) { Fail "Hikvision SDK DLL'leri yok: $sdkLib" }
    if (-not (Get-ChildItem (Join-Path $root 'ffmpeg') -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue)) {
        Fail "FFmpeg DLL'leri yok: ffmpeg\ (tools\get-ffmpeg.ps1)"
    }

    # ---- 3. Tests ----
    if (-not $SkipTests) {
        Step "Testler"
        Invoke-Checked 'dotnet' @('test', 'tests\SPVision.Core.Tests\SPVision.Core.Tests.csproj', '-c', 'Release', '--nologo')
    }

    # ---- 4. Publish ----
    Step "dotnet publish (self-contained, win-x64)"
    $out = Join-Path $root "artifacts\release\$version"
    $publish = Join-Path $out 'publish'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force $publish | Out-Null
    Invoke-Checked 'dotnet' @('publish', 'src\SPVision\SPVision.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-o', $publish, '--nologo')
    foreach ($file in @('SPVision.exe', 'HCNetSDK.dll', 'PlayCtrl.dll', 'HCCore.dll')) {
        if (-not (Test-Path (Join-Path $publish $file))) { Fail "Yayın çıktısında $file yok." }
    }
    if (-not (Get-ChildItem (Join-Path $publish 'HCNetSDKCom') -Filter '*.dll' -ErrorAction SilentlyContinue)) { Fail "Yayın çıktısında HCNetSDKCom\*.dll yok." }
    if (-not (Get-ChildItem (Join-Path $publish 'FFmpeg') -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue)) { Fail "Yayın çıktısında FFmpeg\avcodec-*.dll yok." }
    $productVersion = (Get-Item (Join-Path $publish 'SPVision.exe')).VersionInfo.ProductVersion
    if (-not $productVersion.StartsWith($version)) { Fail "SPVision.exe sürümü $productVersion, beklenen $version." }

    # ---- 5. Installer ----
    Step "Inno Setup"
    Invoke-Checked $iscc @('/Qp', "/DAppVersion=$version", "/DPublishDir=$publish", "/DOutputDir=$out", (Join-Path $root 'installer\SPVision.iss'))
    $installer = Join-Path $out "SPVision-Kurulum-$version.exe"
    if (-not (Test-Path $installer)) { Fail "Kurulum dosyası oluşmadı: $installer" }

    # ---- 6. Manifest + signature ----
    Step "manifest.json + imza"
    $signer = Join-Path $root 'tools\release\UpdateSigner\UpdateSigner.csproj'
    $manifest = Join-Path $out 'manifest.json'
    $body = Join-Path $out 'notes.md'
    Invoke-Checked 'dotnet' @('run', '--project', $signer, '-c', 'Release', '--', 'manifest', '--installer', $installer, '--notes', $notes,
        '--version', $version, '--out', $manifest, '--notes-out', $body)
    $signArgs = @('run', '--project', $signer, '-c', 'Release', '--', 'sign', '--manifest', $manifest, '--key-file', $SigningKey)
    $verifyArgs = @('run', '--project', $signer, '-c', 'Release', '--', 'verify', '--manifest', $manifest, '--installer', $installer)
    if ($KeyRotation) {
        $signArgs += '--key-rotation'
        $verifyArgs += @('--key-file', $SigningKey)
    }
    Invoke-Checked 'dotnet' $signArgs
    Invoke-Checked 'dotnet' $verifyArgs

    $files = @($installer, $manifest, "$manifest.sig")
    Write-Host ""
    Write-Host "Hazır: $out" -ForegroundColor Green
    $files | ForEach-Object { Write-Host ("  {0}  ({1:N0} bayt)" -f (Split-Path $_ -Leaf), (Get-Item $_).Length) }
    if ($DryRun) {
        Write-Host "DryRun: GitHub'a hiçbir şey yüklenmedi." -ForegroundColor Yellow
        exit 0
    }

    # ---- 7. GitHub release ----
    Step "GitHub sürümü"
    if (-not $Yes) {
        $answer = Read-Host "SPVision $version ($tag, commit $($commit.Substring(0, 7))) yayınlansın mı? Kurulu tüm SPVision'lar bunu görecek. (evet/hayır)"
        if ($answer -ne 'evet') {
            Write-Host "Yayınlanmadı."
            exit 0
        }
    }
    Invoke-Checked 'gh' (@('release', 'create', $tag) + $files + @('--repo', $Repo, '--target', $commit, '--title', "SPVision $version",
        '--notes-file', $body, '--latest'))

    # gh can silently skip a file on a transient error: check, upload once more, then give up loudly.
    $names = Get-Native { gh release view $tag --repo $Repo --json assets -q '.assets[].name' }
    foreach ($file in $files) {
        if ($names -notcontains (Split-Path $file -Leaf)) {
            Warn "$(Split-Path $file -Leaf) sürümde yok; yeniden yükleniyor."
            Invoke-Checked 'gh' @('release', 'upload', $tag, $file, '--repo', $Repo, '--clobber')
        }
    }
    $names = Get-Native { gh release view $tag --repo $Repo --json assets -q '.assets[].name' }
    foreach ($file in $files) {
        if ($names -notcontains (Split-Path $file -Leaf)) { Fail "$(Split-Path $file -Leaf) sürüme yüklenemedi; GitHub'da elle ekleyin." }
    }
    $null = Test-Native { git fetch origin --tags --quiet }
    Write-Host ""
    Write-Host "Yayınlandı: SPVision $version. Kurulu kopyalar en geç 6 saat içinde (ya da Ayarlar › Güncellemeler › Denetle ile hemen) görür." -ForegroundColor Green
}
finally {
    Pop-Location
}
