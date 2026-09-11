#!/usr/bin/env bash
# package.sh — Genera el instalador .exe (Windows) sin necesidad de Windows/Inno.
#   1. Publica la app self-contained (artifacts/win-x64/Alarmino.exe)
#   2. Publica el stub instalador (self-extracting)
#   3. Compone artifacts/AlarminoSetup.exe = stub + payload.zip + marca
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

./scripts/publish.sh

echo "Publicando el stub del instalador..."
dotnet publish tools/Alarmino.Installer/Alarmino.Installer.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o artifacts/installer-stub --nologo

echo "Componiendo instalador..."
python3 scripts/compose-installer.py \
    artifacts/installer-stub/Alarmino.Installer.exe \
    artifacts/win-x64 \
    artifacts/AlarminoSetup.exe

echo "INSTALADOR LISTO: artifacts/AlarminoSetup.exe"