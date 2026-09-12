<#
    Memasang Screen Time Guard di komputer anak.

    Yang dilakukan:
      1. Menyalin ScreenTimeGuard.exe ke C:\Program Files\ScreenTimeGuard
      2. Membuat folder data C:\ProgramData\ScreenTimeGuard dengan izin terkunci
         (anak hanya boleh membaca, tidak boleh mengubah)
      3. Meminta password orang tua (kalau belum pernah diatur)
      4. Mendaftarkan 2 Scheduled Task:
         - Agent  : berjalan sebagai SYSTEM saat komputer menyala, dicek ulang tiap 5 menit
         - UI Tray: berjalan saat pengguna mana pun login, dicek ulang tiap 5 menit
      5. Menjalankan keduanya sekarang juga

    Jalankan sebagai Administrator:
      klik kanan -> Run with PowerShell   (script akan minta elevasi sendiri)
#>

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- elevasi diri
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Meminta hak Administrator..." -ForegroundColor Yellow
    $psi = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    Start-Process -FilePath 'powershell.exe' -ArgumentList $psi -Verb RunAs
    return
}

$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcExe    = Join-Path $root 'bin\ScreenTimeGuard.exe'
$installDir= Join-Path $env:ProgramFiles 'ScreenTimeGuard'
$exe       = Join-Path $installDir 'ScreenTimeGuard.exe'
$dataDir   = Join-Path $env:ProgramData 'ScreenTimeGuard'

$taskAgent = 'ScreenTimeGuard Agent'
$taskUi    = 'ScreenTimeGuard Tray'

Write-Host "=== Screen Time Guard - pemasangan ===" -ForegroundColor Cyan

# ------------------------------------------------------------------- 1. build
if (-not (Test-Path $srcExe)) {
    Write-Host "ScreenTimeGuard.exe belum ada, menjalankan build.ps1 ..." -ForegroundColor Yellow
    & (Join-Path $root 'build.ps1')
}
if (-not (Test-Path $srcExe)) { throw "Gagal menemukan $srcExe" }

# ------------------------------------------------------------------ 2. salin
Write-Host "[1/5] Menyalin program ke $installDir"
foreach ($t in @($taskAgent, $taskUi)) {
    if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
    }
}
Get-Process -Name 'ScreenTimeGuard' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir -Force | Out-Null }
Copy-Item -Path $srcExe -Destination $exe -Force

# ------------------------------------------------------- 3. folder data + izin
Write-Host "[2/5] Menyiapkan folder data dan mengunci izinnya"
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }

# SYSTEM & Administrators: penuh. Users: hanya baca (agar UI bisa membaca status.json).
& icacls.exe $dataDir /inheritance:r                    | Out-Null
& icacls.exe $dataDir /grant '*S-1-5-18:(OI)(CI)F'      | Out-Null   # SYSTEM
& icacls.exe $dataDir /grant '*S-1-5-32-544:(OI)(CI)F'  | Out-Null   # Administrators
& icacls.exe $dataDir /grant '*S-1-5-32-545:(OI)(CI)RX' | Out-Null   # Users - baca saja
if ($LASTEXITCODE -ne 0) { Write-Host "  Peringatan: sebagian izin gagal diterapkan." -ForegroundColor Yellow }

# --------------------------------------------------------------- 4. password
$settings = Join-Path $dataDir 'settings.json'
$needPassword = $true
if (Test-Path $settings) {
    try {
        $json = Get-Content $settings -Raw | ConvertFrom-Json
        if ($json.PasswordHash) { $needPassword = $false }
    } catch { }
}

if ($needPassword) {
    Write-Host "[3/5] Membuat password orang tua (jendela akan terbuka)"
    & $exe --setup | Out-Null
    $ok = $false
    if (Test-Path $settings) {
        try {
            $json = Get-Content $settings -Raw | ConvertFrom-Json
            if ($json.PasswordHash) { $ok = $true }
        } catch { }
    }
    if (-not $ok) { throw "Password belum diatur. Pemasangan dibatalkan." }
} else {
    Write-Host "[3/5] Password orang tua sudah ada, dilewati."
    Write-Host "      (untuk mengganti: jalankan `"$exe`" --setup sebagai Administrator)"
}

# ------------------------------------------------------- 5. daftar scheduled task
Write-Host "[4/5] Mendaftarkan Scheduled Task"

$agentXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Screen Time Guard - pengawas waktu layar anak (berjalan sebagai SYSTEM).</Description>
    <URI>\$taskAgent</URI>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </BootTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>99</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"$exe"</Command>
      <Arguments>--agent</Arguments>
    </Exec>
  </Actions>
</Task>
"@

$uiXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Screen Time Guard - ikon tray penampil sisa waktu.</Description>
    <URI>\$taskUi</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT10S</Delay>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"$exe"</Command>
      <Arguments>--ui</Arguments>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $taskAgent -Xml $agentXml -Force | Out-Null
Register-ScheduledTask -TaskName $taskUi    -Xml $uiXml    -Force | Out-Null

# ------------------------------------------------------------------ 6. jalankan
Write-Host "[5/5] Menjalankan pengawas"
Start-ScheduledTask -TaskName $taskAgent
Start-Sleep -Seconds 3
Start-ScheduledTask -TaskName $taskUi

$agentRunning = $null -ne (Get-Process -Name 'ScreenTimeGuard' -ErrorAction SilentlyContinue)

Write-Host ""
if ($agentRunning) {
    Write-Host "Pemasangan selesai." -ForegroundColor Green
} else {
    Write-Host "Pemasangan selesai, tetapi agent belum terlihat berjalan." -ForegroundColor Yellow
    Write-Host "Periksa $dataDir\log.txt atau mulai ulang komputer."
}

Write-Host ""
Write-Host "Langkah selanjutnya:" -ForegroundColor Cyan
Write-Host "  1. Klik kanan ikon jam di system tray -> 'Panel orang tua...' -> masukkan password."
Write-Host "  2. Tambahkan aplikasi yang mau dibatasi (contoh: RobloxPlayerBeta.exe) beserta jatah menitnya."
Write-Host ""
Write-Host "PENTING - agar anak tidak bisa mematikan pengawas:" -ForegroundColor Yellow
Write-Host "  Pastikan akun Windows yang dipakai anak adalah akun 'Standard', BUKAN Administrator."
Write-Host "  Cek lewat: Settings > Accounts > Family & other users."
Write-Host ""
Write-Host "Folder data  : $dataDir"
Write-Host "Program      : $exe"
Write-Host "Copot pasang : uninstall.ps1"
Write-Host ""
Read-Host "Tekan Enter untuk menutup"
