#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd -- "$script_dir/../.." && pwd)"
# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$root/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
tcs_c=("$root/scripts/tcs.sh" c)
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/tcs2c-digests.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

kernels=(sprite_update spawn_churn particles)
entries=(SpriteUpdate SpawnChurn Particles)
expected=(e8814b32 9274159d 8bf97e09)

for i in "${!kernels[@]}"; do
  source_file="$root/tests/tcs/DigestKernels/${kernels[$i]}.cs"
  c_file="$work_dir/${kernels[$i]}.c"
  executable="$work_dir/${kernels[$i]}"

  "${tcs_c[@]}" --digest-f32 --entry "${entries[$i]}" \
    "$source_file" -o "$c_file"
  "$cc_cmd" -O2 -ffp-contract=off -fwrapv \
    -fexcess-precision=standard "$c_file" -o "$executable"
  actual="$($executable)"
  if [[ "$actual" != "${expected[$i]}" ]]; then
    echo "${kernels[$i]}: expected ${expected[$i]}, got $actual" >&2
    exit 1
  fi
  echo "${kernels[$i]}: $actual"
done
