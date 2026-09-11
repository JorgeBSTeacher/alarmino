#Requires -Version 5.1
<#
  gen-icon.ps1 — Regenera el icono binario (require Python 3).
  Uso: powershell -ExecutionPolicy Bypass -File scripts\gen-icon.ps1
#>
$root = Split-Path -Parent $PSScriptRoot
python3 "$root\scripts\gen-icon.py" "$root\src\Alarmino\Resources\alarmino.ico"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Icono regenerado." -ForegroundColor Green