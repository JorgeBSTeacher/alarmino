#Requires -Version 5.1
<#
  package.ps1 — Compila el instalador con Inno Setup.
  Requisito: Inno Setup 6 instalado (https://jrsoftware.org/isinfo.php).
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\package.ps1
  Salida: artifacts\AlarminoSetup.exe
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

& "$root\scripts\publish.ps1"

$iscc = Get-ChildItem "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
                       "C:\Program Files\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $iscc) {
    Write-Host "No se encontró Inno Setup. Instálalo desde https://jrsoftware.org/isinfo.php" -ForegroundColor Red
    exit 1
}

& $iscc.FullName "$root\installer\AlarminoSetup.iss"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "OK: artifacts\AlarminoSetup.exe" -ForegroundColor Green