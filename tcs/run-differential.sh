#!/bin/bash
# semantic テストの corpus (TestHelper.CompileAndRunVm) の dotnet differential (C2 後半、T206)。
# tcs VM の stdout を、同じ entry を実 .NET で走らせた stdout と突き合わせる。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOG="$(mktemp)"
trap 'rm -f "$LOG"' EXIT

TCS_DIFFERENTIAL=1 TCS_DIFFERENTIAL_LOG="$LOG" \
  dotnet test "$SCRIPT_DIR/Transpiler.Tests"

echo "--- corpus differential summary ---"
cut -f1 "$LOG" | sort | uniq -c
