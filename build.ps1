<#
    Build Screen Time Guard menjadi satu file ScreenTimeGuard.exe.

    Tidak perlu Visual Studio atau .NET SDK: memakai compiler C# bawaan
    Windows (.NET Framework 4.x) yang sudah ada di setiap Windows 10/11.

    Jalankan:  powershell -ExecutionPolicy Bypass -File build.ps1
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$bin  = Join-Path $root 'bin'
$out  = Join-Path $bin 'ScreenTimeGuard.exe'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "csc.exe (.NET Framework 4.x) tidak ditemukan. Pastikan .NET Framework 4.x terpasang."
}

if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$manifest = Join-Path $bin 'app.manifest'
@'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="ScreenTimeGuard" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}" />
      <supportedOS Id="{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
    </windowsSettings>
  </application>
</assembly>
'@ | Out-File -FilePath $manifest -Encoding utf8

# Versi diambil dari src\Version.cs supaya hanya ada satu sumber kebenaran,
# lalu distempel ke properti file .exe agar updater bisa memeriksanya.
$versionFile = Join-Path $src 'Version.cs'
$m = [regex]::Match((Get-Content $versionFile -Raw), 'Version\s*=\s*"([0-9]+(?:\.[0-9]+){1,3})"')
if (-not $m.Success) { throw "Tidak menemukan nomor versi di $versionFile" }
$version = $m.Groups[1].Value
$fullVersion = $version
while (($fullVersion -split '\.').Count -lt 4) { $fullVersion += '.0' }
Write-Host "Versi: $version"

$asmInfo = Join-Path $bin 'AssemblyInfo.generated.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("Screen Time Guard")]
[assembly: AssemblyProduct("Screen Time Guard")]
[assembly: AssemblyDescription("Pembatas waktu layar per-aplikasi untuk komputer anak")]
[assembly: AssemblyVersion("$fullVersion")]
[assembly: AssemblyFileVersion("$fullVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Out-File -FilePath $asmInfo -Encoding utf8

$sources = @(Get-ChildItem -Path $src -Filter *.cs | ForEach-Object { $_.FullName }) + $asmInfo
if ($sources.Count -eq 0) { throw "Tidak ada file .cs di $src" }

$refs = @(
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Runtime.Serialization.dll'
    '/r:System.Xml.dll'
)

Write-Host "Mengompilasi $($sources.Count) file ke $out ..."

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/optimize+'
    '/platform:anycpu'
    "/win32manifest:$manifest"
    "/out:$out"
) + $refs + $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Kompilasi gagal (exit $LASTEXITCODE)." }

Remove-Item $manifest -Force -ErrorAction SilentlyContinue
Remove-Item $asmInfo -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Selesai: $out (versi $version)" -ForegroundColor Green
Write-Host "Langkah berikutnya: klik kanan install.ps1 -> Run with PowerShell (sebagai Administrator)."
