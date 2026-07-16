#!/usr/bin/env bash
# Full local gate (manual). Mirrors PR CI end to end: the fast commit gate,
# the native gate (build, smokes, physics Lua, goldens, C# samples), then
# WASM build, web build, and headless web verification.
# CI が同じ内容を PR ごとに実行するので普段は不要。CI を待たずに手元で
# 全周りを確認したいとき、または CI が使えないときの脱出ハッチ。
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "${1:-}" in
  -h|--help)
    cat <<'EOF'
Usage: scripts/pre-push.sh

Runs the full local gate:
  the fast commit gate (scripts/pre-commit.sh), the native gate
  (scripts/native-gate.sh: build, smokes, physics Lua, goldens, C#
  samples), WASM build, web build, headless web verification, and
  web goldens (scripts/golden-web.mjs).

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
run bash scripts/native-gate.sh

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
run env LUB_URL="$dev_url" npm --prefix web run golden
