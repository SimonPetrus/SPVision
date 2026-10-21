# Downloads FFmpeg 9 LGPL shared DLLs (BtbN build) into <repo>\ffmpeg.
# FlyleafLib 3.11 expects the FFmpeg 9 ABI.
param(
    [string]$Asset = "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $repo "ffmpeg"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp, $dest | Out-Null
try {
    $zip = Join-Path $tmp $Asset
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$Asset"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $tmp
    $bin = Get-ChildItem $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
    Copy-Item (Join-Path $bin.FullName "*.dll") $dest -Force
    Get-ChildItem $dest -Filter *.dll | ForEach-Object { Write-Host "  $($_.Name)" }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
