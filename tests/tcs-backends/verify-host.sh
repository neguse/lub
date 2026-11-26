#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd -- "$script_dir/../.." && pwd)"
# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$root/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
tcs_c=("$root/scripts/tcs.sh" c)
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-host.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"${tcs_c[@]}" "$script_dir/c/nullable-arguments.cs" -o "$work_dir/nullable.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/nullable.c" -o "$work_dir/nullable"
[[ "$("$work_dir/nullable" | tr -d '\r')" == $'True\nFalse\n7\nTrue\nFalse\n3\n1' ]]

"${tcs_c[@]}" --lib --ref "$script_dir/c/foreign-stub.cs" \
  "$script_dir/c/foreign.cs" -o "$work_dir/foreign.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  -I "$work_dir" "$script_dir/c/foreign-host.c" -o "$work_dir/host"
[[ "$("$work_dir/host" | tr -d '\r')" == $'4\n207\n101:101\n10\n2\ntopic\npayload\n11\n2 3\n2 2 True\nTrue 14 0.25 False 0 0\n4\nTrue 1 2.5 False\n11 -1 -2\n0.5\n4\n21:False:True:1' ]]
echo 'host: typed options, foreign instance methods, enums, nullable defaults, out strings, struct values and outs, and scalar entry arguments passed'

"${tcs_c[@]}" "$script_dir/c/conditional-access.cs" -o "$work_dir/conditional.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/conditional.c" -o "$work_dir/conditional"
[[ "$("$work_dir/conditional" | tr -d '\r')" == $'False\nFalse\nTrue\nTrue\nTrue\nTrue\nFalse\nTrue\n3\nTrue\nFalse\n7\n-1\nTrue\nTrue' ]]

"${tcs_c[@]}" "$script_dir/c/runtime-services.cs" -o "$work_dir/services.c"
"$cc_cmd" -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard \
  "$work_dir/services.c" -o "$work_dir/services"
export TCS_TEST_VALUE='shared runtime'
[[ "$("$work_dir/services" | tr -d '\r')" == $'True\nshared runtime\na\nxba\n2147483647\n-2147483648\nTrue' ]]
echo 'host: conditional access, discarded outputs, inherited options and runtime services passed'
