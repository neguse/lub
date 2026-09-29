#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_cmd="${DOTNET:-dotnet}"
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-game-core.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"$dotnet_cmd" build "$script_dir/tcs2c.csproj" -p:NuGetAudit=false -v:minimal
"$dotnet_cmd" "$script_dir/bin/Debug/net10.0/tcs2c.dll" \
  "$script_dir/tests/game-core.cs" -o "$work_dir/game-core.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/game-core.c" -lm -o "$work_dir/game-core"
actual="$("$work_dir/game-core" | tr -d '\r')"
expected=$'3\n1\n2\n3.5\n9\n0\n6\n3\n3\n0\n1\n0\n8\n-2\n3\n5\n0.5\n1.5\n1\n8\n4\n-1\n3\nbc\n\n1\n1.5\n-2147483648\n4.2949673e9\n6'
if [[ "$actual" != "$expected" ]]; then
  printf 'Game core semantic check failed:\n%s\n' "$actual" >&2
  exit 1
fi
echo 'game-core: arrays, optional arguments, numeric operations and strings passed'

"$dotnet_cmd" "$script_dir/bin/Debug/net10.0/tcs2c.dll" \
  "$script_dir/tests/generics.cs" -o "$work_dir/generics.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/generics.c" -lm -o "$work_dir/generics"
actual="$("$work_dir/generics" | tr -d '\r')"
if [[ "$actual" != $'1\n3\n1\n1\n2\n1' ]]; then
  printf 'Generic pool semantic check failed:\n%s\n' "$actual" >&2
  exit 1
fi
echo 'generics: constrained pools, inherited dispatch and separate static fields passed'

"$dotnet_cmd" "$script_dir/bin/Debug/net10.0/tcs2c.dll" --lib \
  "$script_dir/tests/generics.cs" -o "$work_dir/generics-lib.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/generics-lib.c" "$script_dir/tests/generics-host.c" -o "$work_dir/generics-host"
[[ "$("$work_dir/generics-host" | tr -d '\r')" == '3' ]]
echo 'generics: retained pool survives collection and is reclaimed after release'
