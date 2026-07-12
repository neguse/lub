#!/usr/bin/env bash
# Full local gate for pushes. Mirrors the repository's native + web
# regressions: the fast commit gate first, then visual goldens, WASM
# build, web build, and headless web verification.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/pre-push.sh

Runs the full local pre-push gate:
  the fast commit gate (scripts/pre-commit.sh), visual goldens,
  WASM build, web build, and headless web verification.

npm ci is skipped when web/package.json + web/package-lock.json are
unchanged since the last successful install (stamp in web/node_modules).
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
cleanup() {
  local pid
  for pid in "${cleanup_pids[@]:-}"; do
    if kill -0 "$pid" >/dev/null 2>&1; then
      kill "$pid" >/dev/null 2>&1 || true
      wait "$pid" >/dev/null 2>&1 || true
    fi
  done
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

run bash scripts/pre-commit.sh

native_binary="${LUB_PRECOMMIT_BINARY:-./build-release-linux/lub}"
run_timed env BINARY="$native_binary" scripts/run-golden.sh

# C# (tcs) サンプル: dotnet と third_party/tcs submodule があるときだけ検査する。
# 無い環境では skip (C# 対応は optional toolchain)。
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
  run scripts/run-cs-sample.sh 27_breakout_cs --check
  run scripts/run-cs-sample.sh 27_breakout_cs --build
  cs_png="${TMPDIR:-/tmp}/lub-pre-push-27_breakout_cs.png"
  rm -f "$cs_png"
  run_timed env LUB_BACKEND=sdlgpu scripts/run-headless.sh "$native_binary" \
    27_breakout_cs --capture "$cs_png" --capture-frame 240
  run cmp "$cs_png" tests/golden/27_breakout_cs_sdlgpu.png
else
  echo
  echo "==> C# sample gate skipped (dotnet or third_party/tcs missing)"
fi

if [[ -f "$HOME/emsdk/emsdk_env.sh" ]]; then
  echo
  echo "==> source ~/emsdk/emsdk_env.sh && emcmake cmake -S . -B build/wasm && cmake --build build/wasm -j"
  # shellcheck disable=SC1091
  source "$HOME/emsdk/emsdk_env.sh"
  run_timed emcmake cmake -S . -B build/wasm
  run_timed cmake --build build/wasm -j
else
  echo "missing $HOME/emsdk/emsdk_env.sh; install/source emsdk before pushing" >&2
  exit 1
fi

# npm ci wipes node_modules; only pay that when the manifests changed
# since the last successful install. fetch-slang is idempotent and cheap,
# so always run it to guarantee the slang-wasm vendoring exists.
npm_stamp="web/node_modules/.lub-npm-ci-stamp"
npm_hash="$(cat web/package.json web/package-lock.json | sha256sum | cut -d' ' -f1)"
if [[ -f "$npm_stamp" && "$(cat "$npm_stamp")" == "$npm_hash" ]]; then
  echo
  echo "==> npm ci skipped (web/package.json + package-lock.json unchanged)"
  run npm --prefix web run fetch-slang
else
  run npm --prefix web ci
  printf '%s\n' "$npm_hash" >"$npm_stamp"
fi

run npm --prefix web run build

dev_port="${LUB_PREPUSH_PORT:-5173}"
dev_url="http://127.0.0.1:${dev_port}/"
dev_log="${TMPDIR:-/tmp}/lub-pre-push-vite.log"
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
