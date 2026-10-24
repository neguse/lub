#!/usr/bin/env bash
# lub_serve_smoke (tests/c/serve_smoke.c) を tcs 無しで回す。
# serve_start は tcs を起動して初回の出力を待つので、-o の出力を書いて待つだけの fake を LUB_TCS で渡す (dotnet は要らない)。
# 使い方: scripts/test-serve-smoke.sh [lub_serve_smoke binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub_serve_smoke}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cat > "$work/fake-tcs.sh" <<'FAKE'
while [[ $# -gt 0 ]]; do
  [[ "$1" == -o ]] && out="$2"
  shift
done
printf '__tcs_build = 1\nreturn {}\n' > "$out.tmp" && mv "$out.tmp" "$out"
exec sleep 600
FAKE
LUB_TCS="bash $work/fake-tcs.sh" "$binary"
