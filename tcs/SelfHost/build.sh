#!/bin/bash
# セルフホストの compiler を作り、固定点を確かめる。
#   stage1: 参照実装 (Roslyn の tcs) が SelfHost/Compiler を Lua にしたもの
#   stage2: stage1 (Lua の上) が同じソースを Lua にしたもの
#   stage3: stage2 が同じソースを Lua にしたもの。stage2 と byte で一致すれば固定点
# 出力は SelfHost/out/ (git に入れない)。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/SelfHost/out"
LUA="${TCS_LUA:-$ROOT/deps/lua/lua32}"
TCS="$ROOT/Transpiler/bin/Debug/net10.0/Transpiler.dll"
mkdir -p "$OUT"

if [ ! -f "$TCS" ] || [ -n "${TCS_BUILD:-}" ]; then
  dotnet build "$ROOT/Transpiler" -v q -nologo >/dev/null
fi

# runtime の前置き: 空のプログラムを参照実装で変換した出力 (末尾の型 local の印を除く)
printf '// empty\n' > "$OUT/empty.cs"
dotnet "$TCS" "$OUT/empty.cs" -o "$OUT/empty.lua" >/dev/null
grep -v '^-- tcs:type-locals$' "$OUT/empty.lua" > "$OUT/prelude.lua"

SRC=("$ROOT"/SelfHost/Compiler/*.cs)
dotnet "$TCS" "${SRC[@]}" -o "$OUT/stage1.lua" --entry Compiler >/dev/null
echo "stage1: reference -> $OUT/stage1.lua"

if [ "${1:-}" = "--stage1" ]; then exit 0; fi

"$LUA" "$ROOT/SelfHost/Host/tcsc.lua" "$OUT/stage1.lua" "$OUT/prelude.lua" --time --entry Compiler -o "$OUT/stage2.lua" "${SRC[@]}"
echo "stage2: stage1 -> $OUT/stage2.lua"
"$LUA" "$ROOT/SelfHost/Host/tcsc.lua" "$OUT/stage2.lua" "$OUT/prelude.lua" --time --entry Compiler -o "$OUT/stage3.lua" "${SRC[@]}"
echo "stage3: stage2 -> $OUT/stage3.lua"
if cmp -s "$OUT/stage2.lua" "$OUT/stage3.lua"; then
  echo "fixed point: stage2 == stage3"
else
  echo "NOT a fixed point: stage2 != stage3" >&2
  diff "$OUT/stage2.lua" "$OUT/stage3.lua" | head -40 >&2
  exit 1
fi
