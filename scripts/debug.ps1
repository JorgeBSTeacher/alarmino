#Requires -Version 5.1
<#
  debug.ps1 — Compila en Debug (con símbolos) y ejecuta la app. Ideal desde
  Visual Studio / VS Code con breakpoints.
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\debug.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $IsWindows) {
    Write-Host "Necesitas Windows para ejecutar la app WPF." -ForegroundColor Yellow
    exit 1
}

dotnet build "$root\src\Alarmino\Alarmino.csproj" -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project "$root\src\Alarmino\Alarmino.csproj" -c Debug