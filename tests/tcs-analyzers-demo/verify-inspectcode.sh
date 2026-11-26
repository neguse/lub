#!/bin/bash
set -euo pipefail

# Runs JetBrains InspectCode (the engine of Rider, without the UI) on this project and checks the TinyC# diagnostics in the SARIF.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TOOL_VERSION="2026.1.3"

source "$SCRIPT_DIR/rider-env.sh"

TOOL_DIR="${TCS_JETBRAINS_TOOL_DIR:-$(tcs_cache_dir jetbrains-tools)}"
OUTPUT_DIR="${TCS_INSPECTCODE_OUTPUT_DIR:-$(tcs_cache_dir inspectcode-analyzer-demo)}"
JB="$TOOL_DIR/jb"
SARIF="$OUTPUT_DIR/demo.sarif"
STDOUT_LOG="$OUTPUT_DIR/demo.stdout"

# $JB is executed below, so trust it only after confirming that TOOL_DIR is owned by the current user
ensure_private_dir "$TOOL_DIR"
if [ ! -x "$JB" ]; then
  dotnet tool install JetBrains.ReSharper.GlobalTools \
    --tool-path "$TOOL_DIR" \
    --version "$TOOL_VERSION"
fi

rm -rf "$OUTPUT_DIR"
ensure_private_dir "$OUTPUT_DIR"

run_inspectcode() {
  local project="$1"
  local sarif="$2"
  local stdout_log="$3"

  "$JB" inspectcode "$project" \
    --format=Sarif \
    --output="$sarif" \
    --no-updates \
    --verbosity=ERROR \
    >"$stdout_log" 2>&1
}

count_rule() {
  local sarif="$1"
  local rule_id="$2"
  awk -v rule_id="\"ruleId\": \"$rule_id\"" \
    'index($0, rule_id) { count++ } END { print count + 0 }' \
    "$sarif"
}

assert_file_contains() {
  local label="$1"
  local file="$2"
  local needle="$3"
  if ! grep -Fq -- "$needle" "$file"; then
    echo "Error: InspectCode $label did not contain expected diagnostic text: $needle" >&2
    echo "file: $file" >&2
    exit 1
  fi
}

assert_expected_diagnostic_texts() {
  local label="$1"
  local file="$2"
  for needle in \
      "StructMember" \
      "LocalFunctionStatement" \
      "TryStatement" \
      "ThrowStatement" \
      "ListPattern" \
      "System.IO.File.ReadAllText" \
      "Dictionary<K,V> cannot store null values"; do
    assert_file_contains "$label" "$file" "$needle"
  done
}

assert_expected_counts() {
  local label="$1"
  local sarif="$2"
  local stdout_log="$3"
  local tcs1001_count
  local tcs1002_count
  local tcs1003_count

  tcs1001_count="$(count_rule "$sarif" TCS1001)"
  tcs1002_count="$(count_rule "$sarif" TCS1002)"
  tcs1003_count="$(count_rule "$sarif" TCS1003)"

  if [ "$tcs1001_count" -ne 5 ] || [ "$tcs1002_count" -ne 1 ] || [ "$tcs1003_count" -ne 1 ]; then
    echo "Error: InspectCode $label expected TCS1001 x5 / TCS1002 x1 / TCS1003 x1, got TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count / TCS1003 x$tcs1003_count" >&2
    echo "SARIF: $sarif" >&2
    echo "stdout/stderr log: $stdout_log" >&2
    exit 1
  fi
  assert_expected_diagnostic_texts "$label" "$sarif"

  echo "InspectCode $label diagnostics verified."
  echo "TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count / TCS1003 x$tcs1003_count"
  echo "SARIF: $sarif"
  echo "stdout/stderr log: $stdout_log"
}

run_inspectcode \
  "$SCRIPT_DIR/tcs-analyzers-demo.csproj" \
  "$SARIF" \
  "$STDOUT_LOG"
assert_expected_counts \
  "analyzer demo" \
  "$SARIF" \
  "$STDOUT_LOG"
