#!/usr/bin/env bash
# Full local gate for commits. This is intentionally heavier than a normal
# pre-commit hook because it mirrors the repository's native + web regressions.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/pre-commit.sh

Runs the full local pre-commit gate:
  format, whitespace checks, Release build, visual goldens, WASM build,
  web build, and headless web verification.
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

cleanup_pids=()
cleanup_files=()
cleanup() {
  local pid
  for pid in "${cleanup_pids[@]:-}"; do
    if kill -0 "$pid" >/dev/null 2>&1; then
      kill "$pid" >/dev/null 2>&1 || true
      wait "$pid" >/dev/null 2>&1 || true
    fi
  done
  if [[ ${#cleanup_files[@]} -gt 0 ]]; then
    rm -f "${cleanup_files[@]}"
  fi
}
trap cleanup EXIT INT TERM

wait_for_url() {
  local url="$1"
  local seconds="$2"
  local end=$((SECONDS + seconds))
  while (( SECONDS < end )); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  echo "timed out waiting for $url" >&2
  return 1
}

format_before="$(mktemp)"
format_after="$(mktemp)"
cleanup_files+=("$format_before" "$format_after")
git diff --binary >"$format_before"
git diff --cached --binary >>"$format_before"
run scripts/format.sh
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
run_timed bash scripts/build-release.sh --target lub_haxe_build_smoke --no-configure
run_timed ./build-release-linux/lub_haxe_build_smoke
run_timed bash scripts/build-release.sh --target lub_physics_box2d_smoke --no-configure
run_timed ./build-release-linux/lub_physics_box2d_smoke
physics_lua_tests=(
  tests/lua/test_physics_box2d.lua
  tests/lua/test_physics_box2d_phase2.lua
  tests/lua/test_physics_box2d_phase3.lua
  tests/lua/test_physics_box2d_debug.lua
  tests/lua/test_physics_box2d_joints.lua
  tests/lua/test_physics_box2d_callbacks.lua
  tests/lua/test_physics_box2d_lifetime.lua
)
for physics_lua_test in "${physics_lua_tests[@]}"; do
  run_timed scripts/run-headless.sh "$native_binary" "$physics_lua_test"
done
run_timed env BINARY="$native_binary" scripts/run-golden.sh

if [[ -f "$HOME/emsdk/emsdk_env.sh" ]]; then
  echo
  echo "==> source ~/emsdk/emsdk_env.sh && emcmake cmake -S . -B build/wasm && cmake --build build/wasm -j"
  # shellcheck disable=SC1091
  source "$HOME/emsdk/emsdk_env.sh"
  run_timed emcmake cmake -S . -B build/wasm
  run_timed cmake --build build/wasm -j
else
  echo "missing $HOME/emsdk/emsdk_env.sh; install/source emsdk before committing" >&2
  exit 1
fi

run npm --prefix web ci
run npm --prefix web run build

dev_port="${LUB_PRECOMMIT_PORT:-5173}"
dev_url="http://127.0.0.1:${dev_port}/"
dev_log="${TMPDIR:-/tmp}/lub-pre-commit-vite.log"
rm -f "$dev_log"
echo
echo "==> npm --prefix web run dev -- --host 127.0.0.1 --port $dev_port --strictPort"
npm --prefix web run dev -- --host 127.0.0.1 --port "$dev_port" --strictPort >"$dev_log" 2>&1 &
cleanup_pids+=("$!")
wait_for_url "$dev_url" 45 || {
  echo "vite dev log:" >&2
  tail -80 "$dev_log" >&2 || true
  exit 1
}

run env LUB_URL="$dev_url" npm --prefix web run verify
