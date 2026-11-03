#!/usr/bin/env bash
# lub_serve_smoke (tests/c/serve_smoke.c) を tcs 無しで回す。
# serve_start は tcs を起動して初回の出力を待つので、-o の出力を書いて待つだけの fake を LUB_TCS で渡す (dotnet は要らない)。
# 使い方: scripts/test-serve-smoke.sh [lub_serve_smoke binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub_serve_smoke}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# tcs vm (第 1 引数が vm) のときは bytecode の代わりの byte を書き、少し待ってから差分と compile error の frame を出す。
cat > "$work/fake-tcs.sh" <<'FAKE'
vm=0
[[ "${1:-}" == vm ]] && vm=1
while [[ $# -gt 0 ]]; do
  [[ "$1" == -o ]] && out="$2"
  shift
done
if [[ $vm -eq 1 ]]; then
  printf 'TCSB' > "$out.tmp" && mv "$out.tmp" "$out"
  sleep 1
  printf '@@tcs_vm_batch 8\nQUJDREVG@@tcs_build_failed 5\noops!'
else
  printf '__tcs_build = 1\nreturn {}\n' > "$out.tmp" && mv "$out.tmp" "$out"
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
