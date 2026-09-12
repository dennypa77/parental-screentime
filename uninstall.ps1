<#
    Mencopot Screen Time Guard.

    Jalankan sebagai Administrator: klik kanan -> Run with PowerShell
    (script akan meminta elevasi sendiri).
#>

$ErrorActionPreference = 'Stop'

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Meminta hak Administrator..." -ForegroundColor Yellow
    $psi = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    Start-Process -FilePath 'powershell.exe' -ArgumentList $psi -Verb RunAs
    return
}

$installDir = Join-Path $env:ProgramFiles 'ScreenTimeGuard'
$dataDir    = Join-Path $env:ProgramData 'ScreenTimeGuard'
$taskAgent  = 'ScreenTimeGuard Agent'
$taskUi     = 'ScreenTimeGuard Tray'

Write-Host "=== Screen Time Guard - copot pasang ===" -ForegroundColor Cyan

foreach ($t in @($taskAgent, $taskUi)) {
    if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
        Write-Host "Menghapus Scheduled Task: $t"
        Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $t -Confirm:$false
    }
}

Write-Host "Menghentikan proses yang masih berjalan"
Get-Process -Name 'ScreenTimeGuard' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

if (Test-Path $installDir) {
    Write-Host "Menghapus $installDir"
    Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path $dataDir) {
    $answer = Read-Host "Hapus juga data & riwayat di $dataDir ? (y/N)"
    if ($answer -eq 'y' -or $answer -eq 'Y') {
        & icacls.exe $dataDir /reset /T /C | Out-Null
        Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Data dihapus."
    } else {
        Write-Host "Data dibiarkan di $dataDir"
    }
}

Write-Host ""
Write-Host "Selesai." -ForegroundColor Green
Read-Host "Tekan Enter untuk menutup"
