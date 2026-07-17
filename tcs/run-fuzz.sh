#!/bin/bash
# サブセット内 AST 生成 fuzz (C4、T186)。使い方: run-fuzz.sh [count] [base-seed]
# 生成 → tcs → Lua 実行と実 .NET 実行の differential。失敗は縮小済み再現付きで報告。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
COUNT="${1:-500}"
SEED="${2:-1000}"

TCS_FUZZ=1 TCS_FUZZ_COUNT="$COUNT" TCS_FUZZ_SEED="$SEED" \
  dotnet test "$SCRIPT_DIR/Transpiler.Tests" --filter "FuzzSweep"
