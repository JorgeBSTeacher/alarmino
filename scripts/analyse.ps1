#Requires -Version 5.1
<#
  analyse.ps1 — Analiza el estilo y reglas de código (dotnet format + analizadores).
  Uso (modo CI, no cambia nada):     powershell -File scripts\analyse.ps1 -Verify
  Uso (aplica formato automático):   powershell -File scripts\analyse.ps1 -Fix
#>
[CmdletBinding()]
param([switch]$Verify, [switch]$Fix)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not ($Verify -xor $Fix)) {
    Write-Host "Indica una de las opciones: -Verify  |  -Fix" -ForegroundColor Yellow
    exit 1
}

dotnet format "$root\Alarmino.sln" $(if ($Fix) { "" } else { "--verify-no-changes" })
if ($LASTEXITCODE -ne 0) {
    Write-Host "Análisis con diferencias (ejecuta con -Fix para aplicarlas)." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "Formato y analizadores OK." -ForegroundColor Green