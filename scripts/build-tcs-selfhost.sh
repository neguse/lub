#!/usr/bin/env bash
# セルフホストの tcs compiler (third_party/tcs/SelfHost) を作り、<build-dir>/tcs/selfhost に置く。
# dotnet の無い環境の lub は、この一式を `lub tcsc` で動かして C# を変換する (src/tcs_selfhost.c)。
#   1. 参照実装の tcs (dotnet) が compiler のソースを Lua にする (stage1) と、runtime の前置き (prelude) を作る
#   2. lub の Lua (`lub tcsc`) が stage1 で同じソースを変換する (stage2)。これを配る compiler にする
#   3. stage2 でもう一度変換し (stage3)、stage2 と byte で一致すること (固定点) を確かめる
# 作るのに dotnet SDK と lub の build が要る。使うのには要らない。
#
# Usage: scripts/build-tcs-selfhost.sh [build-dir]   (既定 build-release-linux)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
build="${1:-build-release-linux}"
lub="$build/lub"
tcs=third_party/tcs
out="$build/tcs/selfhost"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

[[ -x "$lub" ]] || { echo "lub not built: $lub" >&2; exit 1; }
dotnet build "$tcs/Transpiler" -c Release -nologo -v q
transpiler="$(find "$tcs/Transpiler/bin/Release" -name Transpiler.dll -print -quit)"

mapfile -t sources < <(find "$tcs/SelfHost/Compiler" -name '*.cs' | sort)
printf '// empty\n' > "$work/empty.cs"
dotnet "$transpiler" "$work/empty.cs" -o "$work/empty.lua" >/dev/null
grep -v '^-- tcs:type-locals$' "$work/empty.lua" > "$work/prelude.lua"
dotnet "$transpiler" "${sources[@]}" -o "$work/stage1.lua" --entry Compiler >/dev/null

# compiler を差し替えて `lub tcsc` を動かす
stage() {
  local compiler="$1" output="$2" dir="$work/run"
  rm -rf "$dir" && mkdir -p "$dir"
  cp "$tcs/SelfHost/Host/tcsc.lua" "$dir/tcsc.lua"
  cp "$compiler" "$dir/compiler.lua"
  cp "$work/prelude.lua" "$dir/prelude.lua"
  cp "$tcs/TinySystem/Linq.cs" "$dir/Linq.cs"
  LUB_TCS_SELFHOST_DIR="$dir" "$lub" tcsc --entry Compiler -o "$output" "${sources[@]}"
}
stage "$work/stage1.lua" "$work/stage2.lua"
stage "$work/stage2.lua" "$work/stage3.lua"
if ! cmp -s "$work/stage2.lua" "$work/stage3.lua"; then
  echo "the self-hosted compiler is not at a fixed point (stage2 != stage3)" >&2
  exit 1
fi

mkdir -p "$out"
cp "$tcs/SelfHost/Host/tcsc.lua" "$out/tcsc.lua"
cp "$work/stage2.lua" "$out/compiler.lua"
cp "$work/prelude.lua" "$out/prelude.lua"
cp "$tcs/TinySystem/Linq.cs" "$out/Linq.cs"
echo "self-hosted tcs compiler: $out (fixed point: stage2 == stage3)"
