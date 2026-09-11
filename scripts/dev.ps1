#Requires -Version 5.1
<#
  dev.ps1 — Compila en Debug y ejecuta Alarmino localmente (solo Windows WPF).
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 [-R]
#>
[CmdletBinding()]
param([switch]$R)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $IsWindows) {
    Write-Host "El ejecutable WPF solo se puede ejecutar en Windows." -ForegroundColor Yellow
    exit 1
}

dotnet build "$root\src\Alarmino\Alarmino.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($R) {
    dotnet run --project "$root\src\Alarmino\Alarmino.csproj" -c Debug
}
Write-Host "OK -> src\Alarmino\bin\Debug\net8.0-windows\Alarmino.exe" -ForegroundColor Green