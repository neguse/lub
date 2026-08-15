#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OPEN_RIDER="$SCRIPT_DIR/open-rider-demo.sh"
VERIFY_INSPECTCODE="$SCRIPT_DIR/verify-inspectcode.sh"
TEMP_DIR="$(mktemp -d)"

source "$SCRIPT_DIR/rider-env.sh"

cleanup() {
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT

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

fake_rider="$TEMP_DIR/rider.sh"
cat >"$fake_rider" <<'EOF'
#!/bin/bash
exit 0
EOF
chmod +x "$fake_rider"

success_output="$(
  TCS_RIDER_COMMAND="$fake_rider" \
  DISPLAY=:0 \
  WAYLAND_DISPLAY= \
  "$OPEN_RIDER" --no-precheck 2>&1
)"
assert_contains "open-rider-demo success path" "$success_output" "Opening "
assert_contains "open-rider-demo success path" "$success_output" "Rider command: $fake_rider"

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
      TCS_RIDER_COMMAND="$fake_rider" \
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

cache_dir_default="$(XDG_CACHE_HOME= HOME="$TEMP_DIR/home" tcs_cache_dir jetbrains-tools)"
if [ "$cache_dir_default" != "$TEMP_DIR/home/.cache/tcs/jetbrains-tools" ]; then
  echo "Error: tcs_cache_dir did not fall back to the per-user cache: $cache_dir_default" >&2
  exit 1
fi

private_dir="$TEMP_DIR/private"
ensure_private_dir "$private_dir"
private_mode="$(stat -c '%a' "$private_dir" 2>/dev/null || stat -f '%Lp' "$private_dir")"
if [ "$private_mode" != "700" ]; then
  echo "Error: ensure_private_dir left mode $private_mode on $private_dir" >&2
  exit 1
fi

mkdir -p "$TEMP_DIR/link-target"
ln -s "$TEMP_DIR/link-target" "$TEMP_DIR/tool-link"
set +e
symlink_output="$(ensure_private_dir "$TEMP_DIR/tool-link" 2>&1)"
symlink_exit=$?
set -e
if [ "$symlink_exit" -eq 0 ]; then
  echo "Error: ensure_private_dir accepted a symlinked directory" >&2
  exit 1
fi
assert_contains "ensure_private_dir symlink path" "$symlink_output" "is a symlink"

set +e
inspectcode_output="$(
  TCS_JETBRAINS_TOOL_DIR="$TEMP_DIR/tool-link" \
  TCS_INSPECTCODE_OUTPUT_DIR="$TEMP_DIR/inspectcode-output" \
  bash "$VERIFY_INSPECTCODE" 2>&1
)"
inspectcode_exit=$?
set -e
if [ "$inspectcode_exit" -eq 0 ]; then
  echo "Error: verify-inspectcode.sh accepted an untrusted tool directory" >&2
  exit 1
fi
assert_contains "verify-inspectcode tool dir guard" "$inspectcode_output" "is a symlink"

echo "Rider helper script tests passed."
