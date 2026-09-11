#Requires -Version 5.1
<#
  publish.ps1 — Genera el .exe portátil self-contained (single-file) para win-x64.
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
  Salida: artifacts\win-x64\Alarmino.exe
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = "$root\artifacts\win-x64"

dotnet publish "$root\src\Alarmino\Alarmino.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out --nologo

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "OK: $out\Alarmino.exe" -ForegroundColor Green