#!/usr/bin/env bash
# lub_serve_smoke (tests/c/serve_smoke.c) を tcs 無しで回す。
# serve_start_vm は tcs vm を起動して最初の出力を待つので、-o の出力を書いて待つだけの fake を LUB_TCS で渡す (dotnet は要らない)。
# 使い方: scripts/test-serve-smoke.sh [lub_serve_smoke binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub_serve_smoke}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# bytecode の代わりの byte を書く。
# --smoke-frames があれば、少し待ってから知らない種類の frame、差分、compile error の frame を 1 回の write で出す。
# 知らない frame の本文は frame の見出しに見える行と UTF-8 の多 byte 文字を持つ (lub が本文を byte 数どおりに飛ばさないと、偽の差分が出る)。
cat > "$work/fake-tcs.sh" <<'FAKE'
export LC_ALL=C # ${#s} を byte 数にする
frames=0
while [[ $# -gt 0 ]]; do
  [[ "$1" == -o ]] && out="$2"
  [[ "$1" == --smoke-frames ]] && frames=1
  shift
done
frame() { printf '@@tcs_%s %d\n%s' "$1" "${#2}" "$2"; }
printf 'TCSB' > "$out.tmp" && mv "$out.tmp" "$out"
if [[ $frames -eq 1 ]]; then
  sleep 1
  printf '%s%s%s' "$(frame future_kind $'@@tcs_vm_batch 5\nWRONG\n\xe2\x9c\x93')" \
    "$(frame vm_batch QUJDREVG)" "$(frame build_failed 'oops!')"
fi
exec sleep 600
FAKE
# Windows の CreateProcess は msys の path を読めず、bash も PATH より System32 (WSL) を先に見るので、空白の無い Windows の path で渡す
bash_cmd=bash
script="$work/fake-tcs.sh"
if command -v cygpath >/dev/null; then
  bash_cmd="$(cygpath -ms "$(command -v bash)")"
  script="$(cygpath -ms "$script")"
fi
LUB_TCS="$bash_cmd $script" "$binary"
