#Requires -Version 5.1
<#
  install-tools.ps1 — Instala herramientas de desarrollo (Windows y Linux).
  Uso: powershell -ExecutionPolicy Bypass -File scripts\install-tools.ps1 [-InstallInno] [-InstallCoverageTools]
#>
[CmdletBinding()]
param([switch]$InstallInno, [switch]$InstallCoverageTools)

Write-Host "Verificando .NET SDK 8..." -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Instala .NET 8 SDK desde https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Red
}

if ($IsWindows -and $InstallInno) {
    Write-Host "Descarga Inno Setup 6 desde https://jrsoftware.org/isdl.php (instala manualmente)."
    Write-Host "Luego usa scripts\package.ps1 para generar el instalador."
}

if ($InstallCoverageTools -and -not (Get-Command reportgenerator -ErrorAction SilentlyContinue)) {
    dotnet tool install -g dotnet-reportgenerator-globaltool
    Write-Host "reportgenerator instalado. Recuerda añadir herramientas a PATH." -ForegroundColor Green
}

Write-Host "Listo."