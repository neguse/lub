#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd -- "$script_dir/../.." && pwd)"
# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$root/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
tcs_c=("$root/scripts/tcs.sh" c)
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-values.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"${tcs_c[@]}" --lib "$script_dir/c/object-values.cs" \
  "$script_dir/c/interfaces.cs" -o "$work_dir/values.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/values.c" "$script_dir/c/object-values-host.c" -o "$work_dir/values"
[[ "$("$work_dir/values" | tr -d '\r')" == $'7\n1.5\nTrue\n42\n3\n4\nTrue\n1\nTrue\n7\n7\n7' ]]
echo 'object values: mixed roots survive collection and are reclaimed after release'
echo 'interfaces: implementations and checked casts passed'

for entry in InvalidUnbox InvalidInterfaceCast; do
  "${tcs_c[@]}" --entry "$entry" "$script_dir/c/invalid-casts.cs" -o "$work_dir/invalid.c"
  "$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
    "$work_dir/invalid.c" -o "$work_dir/invalid"
  if "$work_dir/invalid" > "$work_dir/fault.txt" 2>&1; then
    echo "$entry unexpectedly succeeded" >&2
    exit 1
  fi
  [[ "$(tr -d '\r' < "$work_dir/fault.txt")" == 'TinyC# fault: invalid-cast' ]]
done
echo 'invalid casts: boxed scalar type and interface checked'
