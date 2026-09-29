#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_cmd="${DOTNET:-dotnet}"
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-gc.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"$dotnet_cmd" build "$script_dir/tcs2c.csproj" -p:NuGetAudit=false -v:minimal
"$dotnet_cmd" "$script_dir/bin/Debug/net10.0/tcs2c.dll" --lib \
  "$script_dir/tests/gc.cs" -o "$work_dir/gc.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/gc.c" "$script_dir/tests/gc-host.c" -o "$work_dir/gc"
actual="$("$work_dir/gc" | tr -d '\r')"
expected=$'42\nalive\n7\n128\n42\n19\n23\n29\n31\n31\n42\n10000\ngc: roots survive, cycles reclaimed, heap bounded'
if [[ "$actual" != "$expected" ]]; then
  printf 'GC semantic check failed:\n%s\n' "$actual" >&2
  exit 1
fi
printf '%s\n' "$actual"
