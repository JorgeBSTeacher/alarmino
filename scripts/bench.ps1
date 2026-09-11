#Requires -Version 5.1
<#
  bench.ps1 — Mide rendimiento del scheduler (throughput + latencia de disparo).
  Uso:  powershell -ExecutionPolicy Bypass -File scripts\bench.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet run --project "$root\tools\Alarmino.Bench\Alarmino.Bench.csproj" -c Release