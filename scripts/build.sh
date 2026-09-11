#!/usr/bin/env bash
# build.sh — Compila Release win-x64 (cross-compilation desde Linux).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

exec dotnet build "$ROOT/src/Alarmino/Alarmino.csproj" -c Release -r win-x64 --nologo