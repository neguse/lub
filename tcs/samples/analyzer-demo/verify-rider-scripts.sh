#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OPEN_RIDER="$SCRIPT_DIR/open-rider-demo.sh"

assert_contains() {
  local label="$1"
  local output="$2"
  local needle="$3"
  if ! printf '%s\n' "$output" | grep -Fq -- "$needle"; then
    echo "Error: $label did not contain expected text: $needle" >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

echo "Running Rider helper script tests..."

success_output="$(
  TCS_RIDER_COMMAND=/bin/true \
  DISPLAY=:0 \
  WAYLAND_DISPLAY= \
  "$OPEN_RIDER" --no-precheck 2>&1
)"
assert_contains "open-rider-demo success path" "$success_output" "Opening "
assert_contains "open-rider-demo success path" "$success_output" "Rider command: /bin/true"

set +e
missing_output="$(
  TCS_RIDER_COMMAND=/definitely/missing/rider.sh \
  DISPLAY=:0 \
  WAYLAND_DISPLAY= \
  "$OPEN_RIDER" --no-precheck 2>&1
)"
missing_exit=$?
set -e
if [ "$missing_exit" -eq 0 ]; then
  echo "Error: open-rider-demo missing Rider command path unexpectedly succeeded" >&2
  exit 1
fi
assert_contains "open-rider-demo missing Rider command path" "$missing_output" "Rider command not found"

case "$(uname -s 2>/dev/null || true)" in
  Linux)
    set +e
    no_display_output="$(
      TCS_RIDER_COMMAND=/bin/true \
      DISPLAY= \
      WAYLAND_DISPLAY= \
      "$OPEN_RIDER" --no-precheck 2>&1
    )"
    no_display_exit=$?
    set -e
    if [ "$no_display_exit" -eq 0 ]; then
      echo "Error: open-rider-demo no-display path unexpectedly succeeded" >&2
      exit 1
    fi
    assert_contains "open-rider-demo no-display path" "$no_display_output" "no GUI display"
    ;;
esac

echo "Rider helper script tests passed."
