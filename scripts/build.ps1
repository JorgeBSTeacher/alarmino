#Requires -Version 5.1
<#
  build.ps1 — Compila Alarmino en Release para win-x64 (cross-compile incluido).
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet build "$root\src\Alarmino\Alarmino.csproj" -c Release -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "OK: src\Alarmino\bin\Release\net8.0-windows\win-x64" -ForegroundColor Green