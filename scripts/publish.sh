#!/usr/bin/env bash
# publish.sh — Publica el exe self-contained single-file para win-x64.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/win-x64"

dotnet publish "$ROOT/src/Alarmino/Alarmino.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$OUT" --nologo

echo "OK: $OUT/Alarmino.exe"