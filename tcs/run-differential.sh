#!/bin/bash
# 既存 TranspileAndRun corpus の dotnet differential (C2 後半、T206)。
# luaExpr が C# 式へ翻訳できる呼び出しを実 .NET で評価して突き合わせる。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOG="$(mktemp)"
trap 'rm -f "$LOG"' EXIT

TCS_DIFFERENTIAL=1 TCS_DIFFERENTIAL_LOG="$LOG" \
  dotnet test "$SCRIPT_DIR/Transpiler.Tests"

echo "--- corpus differential summary ---"
cut -f1 "$LOG" | sort | uniq -c
