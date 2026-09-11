#!/usr/bin/env bash
# analyse.sh — dotnet format: -v verifica solo, -f aplica formato.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

mode="--verify-no-changes"
if [[ "${1:-}" == "-f" ]]; then
    mode=""
fi

exec dotnet format "$ROOT/Alarmino.sln" $mode