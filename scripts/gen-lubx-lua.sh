#!/usr/bin/env bash
# cs-lib (lub.Math と lubx の C# 実装) から raw Lua 向けの samples/lubx.lua を
# 生成する。tcs の --module 出力で、`local lubx = require("lubx")` が
# lubx.SpriteBatch 等の型 table を返す (global の定義も同じ)。
# --check は再生成せず、checkin 済みの生成物との差分を検査する (native gate)。
set -euo pipefail
cd "$(dirname "$0")/.."
check=0
[ "${1:-}" = "--check" ] && check=1
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mapfile -t LIB < <(find cs-lib -name '*.cs' ! -name 'lub_stub.cs' | sort)
dotnet run --project third_party/tcs/Transpiler -- "${LIB[@]}" --ref cs-lib/lub_stub.cs --module -o "$tmp/lubx.lua"
# checkin 済みの生成物は scripts/format.sh と同じ整形を通した形にする。stylua は
# 1 回で不動点にならないことがあるので、変わらなくなるまで回す。
for _ in 1 2 3 4; do
  cp "$tmp/lubx.lua" "$tmp/prev.lua"
  env XDG_CONFIG_HOME=/nonexistent npm --prefix web exec --no -- stylua --no-editorconfig "$tmp/lubx.lua"
  cmp -s "$tmp/lubx.lua" "$tmp/prev.lua" && break
done
dst=samples/lubx.lua
if [ "$check" = 1 ]; then
  if ! diff -q "$tmp/lubx.lua" "$dst" > /dev/null; then
    echo "gen-lubx-lua: $dst is stale (run scripts/gen-lubx-lua.sh)"
    exit 1
  fi
else
  cp "$tmp/lubx.lua" "$dst"
  echo "gen-lubx-lua: wrote $dst"
fi
