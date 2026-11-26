#!/bin/bash
# In-subset AST generation fuzz (C4, T186). Usage: run-fuzz.sh [count] [base-seed]
# Generate → differential of tcs VM execution vs real .NET execution. Failures are reported with a shrunk repro.
# The hot reload fuzz (T235: v1/v2 pair generation + invariant checking) also runs against the tcs VM over the same seed range.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
COUNT="${1:-500}"
SEED="${2:-1000}"

TCS_FUZZ=1 TCS_FUZZ_COUNT="$COUNT" TCS_FUZZ_SEED="$SEED" \
  dotnet test "$SCRIPT_DIR" --filter "FuzzSweep"
TCS_FUZZ=1 TCS_FUZZ_COUNT="$COUNT" TCS_FUZZ_SEED="$SEED" \
  dotnet test "$SCRIPT_DIR/../tcs-backends" --filter "FuzzReloadSweep"
