<#
    Membuat rilis baru: build, salin .exe ke folder release\, hitung SHA256,
    lalu tulis release\latest.json yang dibaca updater di komputer anak.

    Alur menerbitkan versi baru:
      1. ubah nomor versi di src\Version.cs   (mis. 1.0.0 -> 1.0.1)
      2. powershell -ExecutionPolicy Bypass -File make-release.ps1
      3. git add -A ; git commit -m "rilis 1.0.1" ; git push
      4. di komputer anak: Panel orang tua -> tab Pembaruan -> Cek pembaruan -> Pasang

    Tanda tangan digital (opsional, tapi disarankan):
      Sekali saja:  .\make-release.ps1 -GenerateKey
      Simpan release\signing-key.xml BAIK-BAIK dan JANGAN di-commit
      (sudah masuk .gitignore). Salin kunci publik yang ditampilkan ke
      Panel orang tua -> Pembaruan -> "Kunci publik RSA".
      Setelah itu setiap rilis otomatis ditandatangani, dan komputer anak
      akan menolak pembaruan yang tidak ditandatangani kunci Anda.
#>

param(
    [string]$Notes = '',
    [switch]$GenerateKey
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$releaseDir = Join-Path $root 'release'
$keyFile    = Join-Path $releaseDir 'signing-key.xml'

if (-not (Test-Path $releaseDir)) { New-Item -ItemType Directory -Path $releaseDir | Out-Null }

# ------------------------------------------------------------ buat kunci saja
if ($GenerateKey) {
    if (Test-Path $keyFile) {
        throw "Kunci sudah ada di $keyFile. Hapus dulu kalau memang mau membuat yang baru " +
              "(pembaruan lama tidak akan bisa diverifikasi lagi)."
    }
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(2048)
    try {
        $rsa.ToXmlString($true) | Out-File $keyFile -Encoding utf8
        $publicXml = $rsa.ToXmlString($false)
        $publicB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($publicXml))
    } finally { $rsa.Dispose() }

    Write-Host "Kunci privat disimpan di: $keyFile" -ForegroundColor Green
    Write-Host "JANGAN commit berkas itu. Buat cadangannya di tempat aman." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Kunci publik (salin ke Panel orang tua -> Pembaruan):" -ForegroundColor Cyan
    Write-Host $publicB64
    return
}

# ------------------------------------------------------------------- 1. build
& (Join-Path $root 'build.ps1')

$versionFile = Join-Path $root 'src\Version.cs'
$m = [regex]::Match((Get-Content $versionFile -Raw), 'Version\s*=\s*"([0-9]+(?:\.[0-9]+){1,3})"')
if (-not $m.Success) { throw "Tidak menemukan nomor versi di $versionFile" }
$version = $m.Groups[1].Value

$builtExe = Join-Path $root 'bin\ScreenTimeGuard.exe'
if (-not (Test-Path $builtExe)) { throw "Tidak menemukan $builtExe" }

# ------------------------------------------------------- 2. salin & hitung hash
$targetName = "ScreenTimeGuard-$version.exe"
$targetPath = Join-Path $releaseDir $targetName
Copy-Item $builtExe $targetPath -Force

$sha  = (Get-FileHash $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $targetPath).Length

# Simpan dua rilis terakhir, bukan hanya yang terbaru.
# CDN GitHub menyimpan cache latest.json sekitar 5 menit, jadi selama itu masih ada
# komputer yang membaca manifest versi sebelumnya; kalau berkasnya sudah dihapus,
# unduhannya gagal 404. Menyimpan versi N-1 membuat masa transisi itu tetap mulus.
$keep = 2
Get-ChildItem $releaseDir -Filter 'ScreenTimeGuard-*.exe' |
    Sort-Object -Property @{ Expression = {
        $v = $_.BaseName -replace '^ScreenTimeGuard-', ''
        $parsed = [version]'0.0.0'
        if ([version]::TryParse($v, [ref]$parsed)) { $parsed } else { [version]'0.0.0' }
    }} -Descending |
    Select-Object -Skip $keep |
    ForEach-Object {
        Write-Host "  membuang rilis lama: $($_.Name)" -ForegroundColor DarkGray
        Remove-Item $_.FullName -Force
    }

# ------------------------------------------------------------ 3. tanda tangan
$signature = ''
if (Test-Path $keyFile) {
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    try {
        $rsa.FromXmlString((Get-Content $keyFile -Raw))
        $data = [Text.Encoding]::UTF8.GetBytes("$version|$sha")
        $sigBytes = $rsa.SignData(
            $data,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $signature = [Convert]::ToBase64String($sigBytes)
    } finally { $rsa.Dispose() }
    Write-Host "Rilis ditandatangani dengan $keyFile" -ForegroundColor Green
} else {
    Write-Host "Tanpa tanda tangan (jalankan '.\make-release.ps1 -GenerateKey' kalau mau)." -ForegroundColor Yellow
}

# --------------------------------------------------------------- 4. manifest
if (-not $Notes) {
    $Notes = "Versi $version."
}

$repoRaw = 'https://raw.githubusercontent.com/dennypa77/parental-screentime/main/release'

$manifest = [ordered]@{
    version   = $version
    url       = "$repoRaw/$targetName"
    sha256    = $sha
    size      = $size
    notes     = $Notes
    signature = $signature
}

$manifestPath = Join-Path $releaseDir 'latest.json'
# UTF-8 tanpa BOM: BOM membuat sebagian parser JSON gagal membaca berkas.
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 4),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "Rilis $version siap." -ForegroundColor Green
Write-Host "  berkas   : release\$targetName  ($([math]::Round($size/1KB,1)) KB)"
Write-Host "  sha256   : $sha"
Write-Host "  manifest : release\latest.json"
Write-Host ""
Write-Host "Langkah terakhir - unggah ke GitHub:" -ForegroundColor Cyan
Write-Host "  git add -A"
Write-Host "  git commit -m `"rilis $version`""
Write-Host "  git push"
Write-Host ""
Write-Host "Setelah ter-push, komputer anak bisa langsung memasangnya lewat"
Write-Host "Panel orang tua -> tab Pembaruan -> Cek pembaruan -> Pasang sekarang."
