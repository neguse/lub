#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
dotnet build third_party/tcs/tcs2c/tcs2c.csproj -c Release
dotnet build tools/lub-gen/lub-gen.csproj -c Release
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs tests/c/tcs_binding.cs -o "$work/game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_binding.cs -o "$work/binding.c"
cp tests/c/tcs_binding.c "$work/test.c"
printf '\nint main(void) { tcs_lib_init(); tcs_entry_BindingTest_main(); return 0; }\n' >> "$work/test.c"
"${CC:-cc}" -std=gnu11 -O2 -fwrapv -ffp-contract=off -fexcess-precision=standard \
  -Iinclude -I"$work" "$work/test.c" -lm -o "$work/test"
[[ "$("$work/test" | tr -d '\r')" == $'0\n7\n123\n1\ntrue\ntrue\n9' ]]
echo 'tcs2c binding: main target, handles, dictionaries, inherited options, nullable XR views and byte views passed'
