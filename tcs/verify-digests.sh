#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
tcs_root="${TCS_ROOT:-$script_dir/../../tcs}"
dotnet_cmd="${DOTNET:-dotnet}"
cc_cmd="${CC:-gcc}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/luoc-digests.XXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT

"$dotnet_cmd" build "$tcs_root/Transpiler/Transpiler.csproj" \
  -p:NuGetAudit=false -v:minimal
"$dotnet_cmd" restore "$script_dir/luoc.csproj" \
  -p:TcsRoot="$tcs_root" -p:NuGetAudit=false -v:minimal
"$dotnet_cmd" build "$script_dir/luoc.csproj" --no-restore \
  -p:TcsRoot="$tcs_root" -p:BuildProjectReferences=false \
  -p:NuGetAudit=false -v:minimal

luoc_dll="$script_dir/bin/Debug/net10.0/luoc.dll"
kernels=(sprite_update spawn_churn particles)
entries=(SpriteUpdate SpawnChurn Particles)
expected=(e8814b32 9274159d 8bf97e09)

for i in "${!kernels[@]}"; do
  source_file="$tcs_root/Transpiler.Tests/DigestKernels/${kernels[$i]}.cs"
  c_file="$work_dir/${kernels[$i]}.c"
  executable="$work_dir/${kernels[$i]}"

  "$dotnet_cmd" "$luoc_dll" --digest-f32 --entry "${entries[$i]}" \
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
