#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TOOL_VERSION="2026.1.3"
TOOL_DIR="${TCS_JETBRAINS_TOOL_DIR:-/tmp/tcs-jetbrains-tools}"
OUTPUT_DIR="${TCS_INSPECTCODE_OUTPUT_DIR:-/tmp/tcs-inspectcode-analyzer-demo}"
JB="$TOOL_DIR/jb"
SARIF="$OUTPUT_DIR/inspectcode.sarif"
STDOUT_LOG="$OUTPUT_DIR/inspectcode.stdout"

if [ ! -x "$JB" ]; then
  mkdir -p "$TOOL_DIR"
  dotnet tool install JetBrains.ReSharper.GlobalTools \
    --tool-path "$TOOL_DIR" \
    --version "$TOOL_VERSION"
fi

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

"$JB" inspectcode "$REPO_ROOT/samples/analyzer-demo/analyzer-demo.csproj" \
  --format=Sarif \
  --output="$SARIF" \
  --no-updates \
  --verbosity=ERROR \
  >"$STDOUT_LOG" 2>&1

count_rule() {
  local rule_id="$1"
  awk -v rule_id="\"ruleId\": \""$rule_id"\"" \
    'index($0, rule_id) { count++ } END { print count + 0 }' \
    "$SARIF"
}

tcs1001_count="$(count_rule TCS1001)"
tcs1002_count="$(count_rule TCS1002)"

if [ "$tcs1001_count" -ne 4 ] || [ "$tcs1002_count" -ne 1 ]; then
  echo "Error: InspectCode expected TCS1001 x4 / TCS1002 x1, got TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count" >&2
  echo "SARIF: $SARIF" >&2
  echo "stdout/stderr log: $STDOUT_LOG" >&2
  exit 1
fi

echo "InspectCode analyzer demo diagnostics verified."
echo "TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count"
echo "SARIF: $SARIF"
echo "stdout/stderr log: $STDOUT_LOG"
