#!/usr/bin/env bash
# Fast local gate for commits: format (changed files only), whitespace
# checks, native Release build, smoke tests, and physics Lua tests.
# The full regression gate (visual goldens, WASM build, web build and
# headless web verification) is covered by web-deploy CI on push; run
# scripts/pre-push.sh manually when needed.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/pre-commit.sh

Runs the fast local pre-commit gate:
  format (changed files only), whitespace checks, Release build,
  smoke tests, and physics Lua tests.

The full gate (visual goldens, WASM build, web build, headless web
verification) runs in web-deploy CI on push; scripts/pre-push.sh is the
manual equivalent.
EOF
    exit 0
    ;;
  "")
    ;;
  *)
    echo "unknown arg: $1" >&2
    exit 2
    ;;
esac

timeout_cmd=()
if command -v timeout >/dev/null 2>&1; then
  timeout_cmd=(timeout 2h)
fi

run() {
  echo
  echo "==> $*"
  "$@"
}

run_timed() {
  echo
  echo "==> $*"
  "${timeout_cmd[@]}" "$@"
}

cleanup_files=()
cleanup() {
  if [[ ${#cleanup_files[@]} -gt 0 ]]; then
    rm -f "${cleanup_files[@]}"
  fi
}
trap cleanup EXIT INT TERM

format_before="$(mktemp)"
format_after="$(mktemp)"
cleanup_files+=("$format_before" "$format_after")
git diff --binary >"$format_before"
git diff --cached --binary >>"$format_before"
run scripts/format.sh --changed
git diff --binary >"$format_after"
git diff --cached --binary >>"$format_after"
if ! cmp -s "$format_before" "$format_after"; then
  echo
  echo "Formatter changed files. Review/stage the formatted changes, then commit again." >&2
  git status --short >&2
  exit 1
fi
run git diff --check
run git diff --cached --check

run_timed bash scripts/build-release.sh
native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
run_timed scripts/run-headless.sh "$native_binary" tests/lua/test_fixed_dt.lua \
  --fixed-dt 0.0125
run_timed bash scripts/build-release.sh --target lub_haxe_build_smoke --no-configure
run_timed ./build-release-linux/lub_haxe_build_smoke
run_timed bash scripts/build-release.sh --target lub_physics_box2d_smoke --no-configure
run_timed ./build-release-linux/lub_physics_box2d_smoke
run_timed bash scripts/build-release.sh --target lub_surfacenets_smoke --no-configure
run_timed ./build-release-linux/lub_surfacenets_smoke
run_timed bash scripts/build-release.sh --target lub_sdf_smoke --no-configure
run_timed ./build-release-linux/lub_sdf_smoke

physics_lua_tests=(
  tests/lua/test_physics_box2d.lua
  tests/lua/test_physics_box2d_phase2.lua
  tests/lua/test_physics_box2d_phase3.lua
  tests/lua/test_physics_box2d_debug.lua
  tests/lua/test_physics_box2d_joints.lua
  tests/lua/test_physics_box2d_callbacks.lua
  tests/lua/test_physics_box2d_lifetime.lua
  tests/lua/test_resource_revision.lua
  tests/lua/test_audio.lua
  tests/lua/test_font.lua
)
echo
echo "==> physics Lua tests (${#physics_lua_tests[@]} in parallel)"
physics_pids=()
physics_logs=()
for i in "${!physics_lua_tests[@]}"; do
  physics_log="$(mktemp)"
  cleanup_files+=("$physics_log")
  physics_logs+=("$physics_log")
  LUB_XVFB_SERVERNUM=$((300 + i)) "${timeout_cmd[@]}" scripts/run-headless.sh \
    "$native_binary" "${physics_lua_tests[$i]}" >"$physics_log" 2>&1 &
  physics_pids+=("$!")
done
physics_failed=0
for i in "${!physics_lua_tests[@]}"; do
  if wait "${physics_pids[$i]}"; then
    echo "PASS ${physics_lua_tests[$i]}"
  else
    echo "FAIL ${physics_lua_tests[$i]}"
    sed 's/^/    /' "${physics_logs[$i]}"
    physics_failed=1
  fi
done
if [[ $physics_failed -ne 0 ]]; then
  exit 1
fi

echo
echo "pre-commit gate OK (full gate: web-deploy CI (push 時) / scripts/pre-push.sh (手動))"
