#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd -- "$script_dir/../.." && pwd)"
# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$root/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
tcs_c=("$root/scripts/tcs.sh" c)
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-gc.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"${tcs_c[@]}" --lib \
  "$script_dir/c/gc.cs" -o "$work_dir/gc.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/gc.c" "$script_dir/c/gc-host.c" -o "$work_dir/gc"
actual="$("$work_dir/gc" | tr -d '\r')"
expected=$'42\nalive\n7\n128\n42\n19\n23\n29\n31\n31\n42\n10000\ngc: roots survive, cycles reclaimed, heap bounded'
if [[ "$actual" != "$expected" ]]; then
  printf 'GC semantic check failed:\n%s\n' "$actual" >&2
  exit 1
fi
printf '%s\n' "$actual"
