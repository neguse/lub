#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUTPUT_DIR="${TCS_RIDER_PRECHECK_OUTPUT_DIR:-/tmp/tcs-rider-verification-precheck}"
SUMMARY="$OUTPUT_DIR/summary.md"
FAILED=0

value_or_unset() {
  local value="$1"
  if [ -n "$value" ]; then
    printf '%s\n' "$value"
  else
    printf '(unset)\n'
  fi
}

command_or_not_found() {
  local command_name="$1"
  if command -v "$command_name" >/dev/null 2>&1; then
    command -v "$command_name"
  else
    printf '(not found)\n'
  fi
}

find_rider_command() {
  if command -v rider >/dev/null 2>&1; then
    command -v rider
    return
  fi

  if command -v jetbrains-rider >/dev/null 2>&1; then
    command -v jetbrains-rider
    return
  fi

  if command -v rider.sh >/dev/null 2>&1; then
    command -v rider.sh
    return
  fi

  find "$HOME/.local/share/JetBrains" \
      "$HOME/.local/share/JetBrains/Toolbox/apps" \
      /opt /usr/local \
      -maxdepth 8 \
      -type f \
      -name rider.sh \
      -perm -u+x \
      2>/dev/null \
    | head -1
}

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

run_check() {
  local label="$1"
  local log_name="$2"
  shift 2
  local log="$OUTPUT_DIR/$log_name"
  local exit_code

  echo "Running $label..."
  set +e
  "$@" >"$log" 2>&1
  exit_code=$?
  set -e

  if [ "$exit_code" -eq 0 ]; then
    printf -- '- `%s`: pass ([log](%s))\n' "$label" "$log" >>"$SUMMARY"
  else
    printf -- '- `%s`: fail, exit %s ([log](%s))\n' "$label" "$exit_code" "$log" >>"$SUMMARY"
    FAILED=1
  fi
}

{
  rider_command="$(find_rider_command || true)"
  echo "# Rider verification prechecks"
  echo
  echo "- Date: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  echo "- OS: $(uname -a)"
  echo "- .NET SDK: $(dotnet --version)"
  echo "- Rider command: $(value_or_unset "$rider_command")"
  echo "- DISPLAY: $(value_or_unset "${DISPLAY-}")"
  echo "- WAYLAND_DISPLAY: $(value_or_unset "${WAYLAND_DISPLAY-}")"
  echo "- XDG_SESSION_TYPE: $(value_or_unset "${XDG_SESSION_TYPE-}")"
  echo "- Xvfb: $(command_or_not_found Xvfb)"
  echo "- xvfb-run: $(command_or_not_found xvfb-run)"
  echo
  echo "## Results"
} >"$SUMMARY"

run_check "bash run-tests.sh" \
  "run-tests.log" \
  bash "$REPO_ROOT/run-tests.sh"

run_check "samples/analyzer-demo/verify-inspectcode.sh" \
  "verify-inspectcode.log" \
  bash "$REPO_ROOT/samples/analyzer-demo/verify-inspectcode.sh"

run_check "dotnet build samples/analyzer-demo/analyzer-demo.csproj --no-incremental" \
  "analyzer-demo-build.log" \
  dotnet build "$REPO_ROOT/samples/analyzer-demo/analyzer-demo.csproj" --no-incremental

{
  echo
  echo "## Expected Rider Diagnostics"
  echo
  echo "- TCS1001 x5: StructDeclaration, LocalFunctionStatement, TryStatement, ThrowStatement, ListPattern"
  echo "- TCS1002 x1: System.IO.File.ReadAllText"
  echo "- TCS1003 x1: List<T> null storage"
} >>"$SUMMARY"

echo "Summary: $SUMMARY"
if [ "$FAILED" -ne 0 ]; then
  exit 1
fi

echo "Rider verification prechecks passed."
