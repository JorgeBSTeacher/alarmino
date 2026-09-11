#!/usr/bin/env bash
# bench.sh — Medidas del scheduler (throughput + latencia).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

exec dotnet run --project "$ROOT/tools/Alarmino.Bench/Alarmino.Bench.csproj" -c Release