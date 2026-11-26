#!/usr/bin/env bash
# Checks the tcs VM glue code (lub-gen vm) against a fake C API (tests/c/tcs_binding.c).
# Builds tests/c/tcs_binding.cs into a .tcsb with tcs vm, and connects the VM with host functions for only the functions that game uses (lub-gen vm --source).
# The output and errors are the same as in the check of the tcs2c glue code (scripts/verify-tcs-binding.sh).
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
dotnet build tcs/Transpiler/Transpiler.csproj -c Release -nologo -v q
dotnet build tools/lub-gen/lub-gen.csproj -c Release -nologo -v q
dotnet tcs/Transpiler/bin/Release/net11.0/Transpiler.dll vm --ref cs-lib/lub_stub.cs \
  --export 'BindingTest.*' tests/c/tcs_binding.cs -o "$work/game.tcsb"
dotnet tools/lub-gen/bin/Release/net11.0/lub-gen.dll vm --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_binding.cs -o "$work/binding.c"
# tests/c/tcs_binding.c also includes the game.c of tcs2c, so make it empty
: > "$work/game.c"
cat tests/c/tcs_binding.c tests/c/vm_binding_driver.c > "$work/test.c"
vm=src/tcs-vm
rt=src/tcs-runtime
"${CC:-cc}" -std=gnu11 -O1 -fwrapv -ffp-contract=off -Iinclude -I"$vm" -I"$work" "$work/test.c" \
  "$vm"/tcsvm_{debug,gc,host,lib,load,reload,rt,run,state}.c "$rt"/sort/*.c "$rt"/fmt/*.c "$rt"/math/*.c "$rt"/utf8/*.c \
  "$rt"/model/*.c -lm -o "$work/test"
[[ "$("$work/test" "$work/game.tcsb" BindingTest.Main | tr -d '\r')" == $'0\n7\n123\n1\nTrue\nTrue\n9\n4\nTrue\n48000\n6\nb\nTrue\n2\n8\nTrue\n10\n3\n3 8 1.5 -2\n1.25 -0.5 4\n2.5\nTrue\nTrue\n11\nTrue\n2\n1\n0000000000000abc\n7\n12.5 -3\nTrue image 7\nFalse True\n0.5 1 1.5' ]]
echo 'tcs VM binding: the same results as the tcs2c binding'

# Input errors become VM faults with a C# trace before the C API is called
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

# How struct records are written and passed. Generate the header and glue code from a stub with tests/c/struct_stub.cs appended after the stub
cat cs-lib/lub_stub.cs tests/c/struct_stub.cs > "$work/struct_stub.cs"
mkdir -p "$work/struct/lub"
dotnet tools/lub-gen/bin/Release/net11.0/lub-gen.dll header --stub "$work/struct_stub.cs" -o "$work/struct/lub/lub_api.h"
dotnet tcs/Transpiler/bin/Release/net11.0/Transpiler.dll vm --ref "$work/struct_stub.cs" \
  --export 'StructGame.*' tests/c/struct_test.cs -o "$work/struct/game.tcsb"
dotnet tools/lub-gen/bin/Release/net11.0/lub-gen.dll vm --stub "$work/struct_stub.cs" \
  --source tests/c/struct_test.cs -o "$work/struct/binding.c"
: > "$work/struct/game.c"
cat tests/c/struct_test.c tests/c/vm_binding_driver.c > "$work/struct/test.c"
"${CC:-cc}" -std=gnu11 -O1 -fwrapv -ffp-contract=off -I"$work/struct" -Iinclude -I"$vm" "$work/struct/test.c" \
  "$vm"/tcsvm_{debug,gc,host,lib,load,reload,rt,run,state}.c "$rt"/sort/*.c "$rt"/fmt/*.c "$rt"/math/*.c "$rt"/utf8/*.c \
  "$rt"/model/*.c -lm -o "$work/struct/test"
[[ "$("$work/struct/test" "$work/struct/game.tcsb" StructGame.Main | tr -d '\r')" == $'13 0.5 True 7 9\n0 False\nTrue 8 True -4 True 2\nFalse 0 False\nFalse 4' ]]
echo 'tcs VM binding: struct records pass by value, as outs and as results, and as record fields'
