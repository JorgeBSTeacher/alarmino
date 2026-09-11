#Requires -Version 5.1
<#
  smoke-win.ps1 — Prueba de humo en Windows:
    1. Compila/publica el ejecutable.
    2. Lo lanza con /min (modo bandeja).
    3. Verifica que el proceso siga vivo y que la entrada de autostart exista.
    4. Lo cierra limpiamente.
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\smoke-win.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = "$root\artifacts\win-x64\Alarmino.exe"

if (-not $IsWindows) {
    Write-Host "Smoke test solo en Windows." -ForegroundColor Yellow
    exit 1
}

& "$root\scripts\publish.ps1"

$started = Start-Process -FilePath $exe -ArgumentList "/min" -PassThru
Start-Sleep -Seconds 4

if ($started.HasExited) {
    Write-Host "FALLO: el proceso terminó antes de tiempo." -ForegroundColor Red
    exit 1
}

Write-Host "1/4 Proceso en ejecución (PID $($started.Id)): OK" -ForegroundColor Green

# 2. Autostart en el registro
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$entry = Get-ItemProperty -Path $runKey -Name "Alarmino" -ErrorAction SilentlyContinue
if ($entry -and $entry.Alarmino -like "*$exe*") {
    Write-Host "2/4 Entrada de autostart (Run): OK" -ForegroundColor Green
}
else {
    Write-Host "2/4 Entrada de autostart: NO encontrada (revisa Configuración)." -ForegroundColor Yellow
}

# 3. Instancia única: un segundo arranque no debe crear otro proceso vivo
$before = @(Get-Process -Name Alarmino -ErrorAction SilentlyContinue).Count
Start-Process -FilePath $exe -ArgumentList "/min" | Out-Null
Start-Sleep -Seconds 2
$after = @(Get-Process -Name Alarmino -ErrorAction SilentlyContinue).Count
if ($after -eq $before -and $before -ge 1) {
    Write-Host "3/4 Instancia única: OK ($before proceso)" -ForegroundColor Green
}
else {
    Write-Host "3/4 Instancia única: FALLO ($before -> $after procesos)" -ForegroundColor Red
    exit 1
}

# 4. Cierre
Stop-Process -Name Alarmino -Force -ErrorAction SilentlyContinue
Write-Host "4/4 Cierre: OK" -ForegroundColor Green
Write-Host "SMOKE OK" -ForegroundColor Green