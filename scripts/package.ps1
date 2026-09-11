#Requires -Version 5.1
<#
  package.ps1 — Genera el instalador .exe (no requiere Windows adicional ni Inno Setup).
  Usa el stub self-extracting de tools\Alarmino.Installer.
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\package.ps1
  Salida: artifacts\AlarminoSetup.exe
  Opcional (si Inno Setup está instalado): -WithInno para generar un instalador Inno Setup adicional.
#>
[CmdletBinding()]
param([switch]$WithInno)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & "$PSScriptRoot\publish.ps1"

    Write-Host "Publicando el stub del instalador..." -ForegroundColor Cyan
    dotnet publish "tools\Alarmino.Installer\Alarmino.Installer.csproj" `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o "artifacts\installer-stub" --nologo

    $python = (Get-Command python -ErrorAction SilentlyContinue) ?? (Get-Command python3 -ErrorAction SilentlyContinue)
    if (-not $python) {
        Write-Host "Necesitas Python 3 para componer el instalador." -ForegroundColor Red
        exit 1
    }

    Write-Host "Componiendo instalador..." -ForegroundColor Cyan
    & $python.Source "scripts\compose-installer.py" `
        "artifacts\installer-stub\Alarmino.Installer.exe" `
        "artifacts\win-x64" `
        "artifacts\AlarminoSetup.exe"

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($WithInno) {
        $iscc = Get-ChildItem "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
                               "C:\Program Files\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($iscc) {
            & $iscc.FullName "installer\AlarminoSetup.iss"
        }
        else {
            Write-Host "Inno Setup no encontrado; omite -WithInno." -ForegroundColor Yellow
        }
    }

    Write-Host "INSTALADOR LISTO: artifacts\AlarminoSetup.exe" -ForegroundColor Green
}
finally {
    Pop-Location
}