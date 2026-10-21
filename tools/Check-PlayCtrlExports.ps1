# PlayCtrl.dll export tablosunu ayristirir ve ihtiyacimiz olan PlayM4_* fonksiyonlarini kontrol eder.
# Kullanim: powershell -NoProfile -ExecutionPolicy Bypass -File Check-PlayCtrlExports.ps1
$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot '..\sdk\EN-HCNetSDKV6.1.11.40_build20260922_win64\lib\PlayCtrl.dll'
$b = [IO.File]::ReadAllBytes((Resolve-Path $dll).Path)

$pe   = [BitConverter]::ToInt32($b, 0x3C)
$opt  = $pe + 0x18
$expRva = [BitConverter]::ToInt32($b, $opt + 0x70)
$nsec = [BitConverter]::ToUInt16($b, $pe + 6)
$optSize = [BitConverter]::ToUInt16($b, $pe + 0x14)
$sec  = $opt + $optSize

$delta = $null
for ($i = 0; $i -lt $nsec; $i++) {
    $s  = $sec + 40 * $i
    $va = [BitConverter]::ToInt32($b, $s + 12)
    $vs = [BitConverter]::ToInt32($b, $s + 8)
    $rw = [BitConverter]::ToInt32($b, $s + 20)
    if ($expRva -ge $va -and $expRva -lt ($va + $vs)) { $delta = $va - $rw; break }
}
if ($null -eq $delta) { throw 'Export dizini bir bolumun icinde degil!' }

$exp = $expRva - $delta
$nNames = [BitConverter]::ToInt32($b, $exp + 24)
$namesArr = ([BitConverter]::ToInt32($b, $exp + 32)) - $delta

$names = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $nNames; $i++) {
    $rva = [BitConverter]::ToInt32($b, $namesArr + 4 * $i)
    $o = $rva - $delta
    $e = $o
    while ($e -lt $b.Length -and $b[$e] -ne 0) { $e++ }
    $names.Add([Text.Encoding]::ASCII.GetString($b, $o, $e - $o))
}

Write-Output ("Toplam export sayisi: " + $names.Count)

$want = @(
    'PlayM4_GetPort','PlayM4_FreePort','PlayM4_OpenStream','PlayM4_InputData','PlayM4_CloseStream',
    'PlayM4_SetStreamOpenMode','PlayM4_SetSecretKey','PlayM4_SetDecodeEngine','PlayM4_SetDecodeEngineEx',
    'PlayM4_Play','PlayM4_Stop','PlayM4_Pause','PlayM4_Fast','PlayM4_Slow','PlayM4_RefreshPlay',
    'PlayM4_GetPictureSize','PlayM4_GetJPEG','PlayM4_SetDisplayBuf','PlayM4_SetOverlayMode',
    'PlayM4_PlaySound','PlayM4_StopSound','PlayM4_SetDecodeFrameType','PlayM4_ReversePlay','PlayM4_OneByOne'
)
foreach ($w in $want) {
    if ($names -contains $w) { Write-Output ('OK   ' + $w) } else { Write-Output ('MISS ' + $w) }
}

Write-Output '--- Engine/Hard/GPU/Accel iceren exportlar:'
$names | Where-Object { $_ -match 'Engine|Hard|GPU|Accel' } | Sort-Object -Unique
