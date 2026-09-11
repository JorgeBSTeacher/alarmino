#!/usr/bin/env bash
# test.sh — Tests unitarios + cobertura (funciona en Linux y Windows).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

exec dotnet test "$ROOT/Alarmino.sln" --collect:"XPlat Code Coverage" \
    --results-directory "$ROOT/artifacts/coverage" --nologo