#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_cmd="${DOTNET:-dotnet}"
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-host.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"$dotnet_cmd" build "$script_dir/tcs2c.csproj" -p:NuGetAudit=false -v:minimal
compiler="$script_dir/bin/Debug/net10.0/tcs2c.dll"
"$dotnet_cmd" "$compiler" "$script_dir/tests/nullable-arguments.cs" -o "$work_dir/nullable.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/nullable.c" -o "$work_dir/nullable"
[[ "$("$work_dir/nullable" | tr -d '\r')" == $'true\nfalse\n7\ntrue\nfalse\n3\n1' ]]

"$dotnet_cmd" "$compiler" --lib --ref "$script_dir/tests/foreign-stub.cs" \
  "$script_dir/tests/foreign.cs" -o "$work_dir/foreign.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  -I "$work_dir" "$script_dir/tests/foreign-host.c" -o "$work_dir/host"
[[ "$("$work_dir/host" | tr -d '\r')" == $'4\n207\n42:42:51:101:-2:1\n10\n2\ntopic\npayload\n0.5\n4\n21:false:true:1' ]]
echo 'host: typed options, foreign instance methods with user subclasses, enums, nullable defaults, out strings and scalar entry arguments passed'

"$dotnet_cmd" "$compiler" "$script_dir/tests/conditional-access.cs" -o "$work_dir/conditional.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/conditional.c" -o "$work_dir/conditional"
[[ "$("$work_dir/conditional" | tr -d '\r')" == $'false\nfalse\ntrue\ntrue\ntrue\ntrue\nfalse\ntrue\n3\ntrue\nfalse\n7\n-1\ntrue\ntrue' ]]

"$dotnet_cmd" "$compiler" "$script_dir/tests/runtime-services.cs" -o "$work_dir/services.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/services.c" -o "$work_dir/services"
export TCS_TEST_VALUE='shared runtime'
[[ "$("$work_dir/services" | tr -d '\r')" == $'true\nshared runtime\na\nxba\n2147483647\n-2147483648\ntrue' ]]
echo 'host: conditional access, discarded outputs, inherited options and runtime services passed'
