#!/bin/bash
# dotnet differential of the semantic test corpus (TestHelper.CompileAndRunVm) (second half of C2, T206).
# Checks the tcs VM stdout against the stdout of running the same entry on real .NET.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOG="$(mktemp)"
trap 'rm -f "$LOG"' EXIT

TCS_DIFFERENTIAL=1 TCS_DIFFERENTIAL_LOG="$LOG" \
  dotnet test "$SCRIPT_DIR/Transpiler.Tests"

echo "--- corpus differential summary ---"
cut -f1 "$LOG" | sort | uniq -c
