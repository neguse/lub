#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
cflags=(-std=gnu11 -O2 -fwrapv -ffp-contract=off -fexcess-precision=standard -Iinclude -I"$work")
dotnet build third_party/tcs/tcs2c/tcs2c.csproj -c Release
dotnet build tools/lub-gen/lub-gen.csproj -c Release
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs tests/c/tcs_binding.cs -o "$work/game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_binding.cs -o "$work/binding.c"
cp tests/c/tcs_binding.c "$work/test.c"
printf '\nint main(void) { tcs_lib_init(); tcs_entry_BindingTest_main(); return 0; }\n' >> "$work/test.c"
"${CC:-cc}" "${cflags[@]}" "$work/test.c" -lm -o "$work/test"
[[ "$("$work/test" | tr -d '\r')" == $'0\n7\n123\n1\ntrue\ntrue\n9\n4\n48000\n6\nb\ntrue\n2\n8\ntrue\ntrue\n10\n3' ]]
echo 'tcs2c binding: main target, handles, dictionaries, inherited options, nullable XR views, byte views, record results, record lists, list results, maybe results and view methods passed'

# tcs2c の生成 C の Math が lub_math を通り、Lua の経路と同じ結果になること。
# 期待値は tests/lua/test_math_determinism.lua の表 (OS ごとに分けない 1 つ)。
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll tests/c/tcs_math.cs -o "$work/math_game.c"
printf '#include "lub_math.h"\n#include "math_game.c"\n' > "$work/math.c"
for source in third_party/musl/src/math/*.c; do
  "${CC:-cc}" "${cflags[@]}" -fno-fast-math -Isrc/musl -include src/musl/libm.h \
    -c "$source" -o "$work/musl_$(basename "$source" .c).o"
done
"${CC:-cc}" "${cflags[@]}" -Isrc "$work/math.c" "$work"/musl_*.o -lm -o "$work/math"
"$work/math" | tr -d '\r' > "$work/math.out"
sed -nE 's/^\t([a-z0-9]+) = (-?[0-9]+),$/\1 = \2/p' tests/lua/test_math_determinism.lua > "$work/math.expected"
[[ -s "$work/math.out" ]]
if grep -vxFf "$work/math.expected" "$work/math.out"; then
  echo 'tcs2c math: the digests above differ from tests/lua/test_math_determinism.lua' >&2
  exit 1
fi
echo "tcs2c math: $(wc -l < "$work/math.out") functions match tests/lua/test_math_determinism.lua"

# cs-lib (lub.Math + lubx) 全体がゲームと同じ経路 (tcs2c → lub-gen tcs → C) を
# 通ること。使う API は多いので C の実装は link せず、compile まで確かめる。
lubx=(cs-lib/lub/Math.cs cs-lib/lubx/*.cs)
lubx_sources=()
for source in "${lubx[@]}"; do lubx_sources+=(--source "$source"); done
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs "${lubx[@]}" -o "$work/lubx_game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  "${lubx_sources[@]}" -o "$work/lubx_binding.c"
printf '#include "lubx_game.c"\n#include "lubx_binding.c"\n' > "$work/lubx.c"
"${CC:-cc}" "${cflags[@]}" -c "$work/lubx.c" -o "$work/lubx.o"
echo "tcs2c binding: cs-lib (${#lubx[@]} files) generated and compiled"
