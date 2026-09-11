#Requires -Version 5.1
<#
  test.ps1 — Ejecuta los tests unitarios con cobertura y genera el reporte.
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\test.ps1
  Opcional: instalar reportgenerator para leer HTML: dotnet tool install -g dotnet-reportgenerator-globaltool
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet test "$root\Alarmino.sln" `
    --collect:"XPlat Code Coverage" `
    --results-directory "$root\artifacts\coverage" `
    --nologo

if (-not (Get-Command reportgenerator -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "Cobertura guardada en artifacts\coverage (usar reportgenerator para el HTML)."
    exit 0
}

$last = Get-ChildItem "$root\artifacts\coverage" -Recurse -Filter "coverage.cobertura.xml" |
    Sort-Object LastWriteTime | Select-Object -Last 1

reportgenerator -reports:$last.FullName -targetdir:"$root\artifacts\coverage\report" -reporttypes:Html
Write-Host "Reporte: artifacts\coverage\report\index.html" -ForegroundColor Green