#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_cmd="${DOTNET:-dotnet}"
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-values.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"$dotnet_cmd" build "$script_dir/tcs2c.csproj" -p:NuGetAudit=false -v:minimal
compiler="$script_dir/bin/Debug/net10.0/tcs2c.dll"
"$dotnet_cmd" "$compiler" --lib "$script_dir/tests/object-values.cs" \
  "$script_dir/tests/interfaces.cs" -o "$work_dir/values.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/values.c" "$script_dir/tests/object-values-host.c" -o "$work_dir/values"
[[ "$("$work_dir/values" | tr -d '\r')" == $'7\n1.5\ntrue\n42\n3\n4\ntrue\n1\ntrue\n7\n7\n7' ]]
echo 'object values: mixed roots survive collection and are reclaimed after release'
echo 'interfaces: inherited implementations and checked casts passed'

for entry in InvalidArrayCast InvalidUnbox InvalidInterfaceCast; do
  "$dotnet_cmd" "$compiler" --entry "$entry" "$script_dir/tests/invalid-casts.cs" -o "$work_dir/invalid.c"
  "$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
    "$work_dir/invalid.c" -o "$work_dir/invalid"
  if "$work_dir/invalid" > "$work_dir/fault.txt" 2>&1; then
    echo "$entry unexpectedly succeeded" >&2
    exit 1
  fi
  [[ "$(tr -d '\r' < "$work_dir/fault.txt")" == 'TinyC# fault: invalid-cast' ]]
done
echo 'invalid casts: array element type, boxed scalar type and interface checked'
