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
[[ "$("$work/test" | tr -d '\r')" == $'0\n7\n123\n1\ntrue\ntrue\n9\n48000\n6\nb\ntrue\n2\n8\ntrue\ntrue\n10\n3' ]]
echo 'tcs2c binding: main target, handles, dictionaries, inherited options, nullable XR views, byte views, record results, record lists, list results, maybe results and view methods passed'

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
