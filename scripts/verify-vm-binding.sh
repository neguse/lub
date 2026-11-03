#!/usr/bin/env bash
# tcs VM の接続コード (lub-gen vm) を偽の C API (tests/c/tcs_binding.c) で検査する。
# tests/c/tcs_binding.cs を tcs vm で .tcsb にし、そのゲームが使う関数だけの host の関数 (lub-gen vm --source) と VM を繋ぐ。
# 出力と誤りは tcs2c の接続コードの検査 (scripts/verify-tcs-binding.sh) と同じ。
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
dotnet build third_party/tcs/Transpiler/Transpiler.csproj -c Release -nologo -v q
dotnet build tools/lub-gen/lub-gen.csproj -c Release -nologo -v q
dotnet third_party/tcs/Transpiler/bin/Release/net10.0/Transpiler.dll vm --ref cs-lib/lub_stub.cs \
  --export 'BindingTest.*' tests/c/tcs_binding.cs -o "$work/game.tcsb"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll vm --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_binding.cs -o "$work/binding.c"
# tests/c/tcs_binding.c は tcs2c の game.c も include するので、空にする
: > "$work/game.c"
cat tests/c/tcs_binding.c tests/c/vm_binding_driver.c > "$work/test.c"
vm=third_party/tcs/tcsvm/vm
rt=third_party/tcs/runtime
"${CC:-cc}" -std=gnu11 -O1 -fwrapv -ffp-contract=off -Iinclude -I"$vm" -I"$work" "$work/test.c" \
  "$vm"/tcsvm_{debug,gc,host,lib,load,reload,rt,run}.c "$rt"/sort/*.c "$rt"/fmt/*.c "$rt"/math/*.c "$rt"/utf8/*.c \
  "$rt"/model/*.c -lm -o "$work/test"
[[ "$("$work/test" "$work/game.tcsb" BindingTest.Main | tr -d '\r')" == $'0\n7\n123\n1\nTrue\nTrue\n9\n4\nTrue\n48000\n6\nb\nTrue\n2\n8\nTrue\nTrue\n10\n3\n2.5\nTrue\nTrue\n11\nTrue\n2\n1\n0000000000000abc\n7' ]]
echo 'tcs VM binding: the same results as the tcs2c binding'

# 入力の誤りは C API を呼ぶ前に、C# の trace の付いた VM の fault になる
for fault in 'EmptyMask:bit mask: empty hex string' 'LongMask:bit mask: more than 16 hex digits' 'ArrayCount:count is out of range for the array'; do
  entry=${fault%%:*}
  if "$work/test" "$work/game.tcsb" "BindingTest.$entry" 2> "$work/$entry.err"; then
    echo "tcs VM binding: $entry did not fault" >&2
    exit 1
  fi
  grep -qF "fault: " "$work/$entry.err"
  grep -qF "${fault#*:}" "$work/$entry.err"
  grep -qF "at BindingTest.$entry" "$work/$entry.err"
done
echo 'tcs VM binding: bad bit masks and a count past the end of the array fault with a C# trace'
