#!/usr/bin/env bash
# Compiles lider itself into tcs VM bytecode (Lider.tcsb).
#   scripts/build-module.sh <out.tcsb>
# Used when running lider by passing the player a .tcsb instead of a .csproj (the web page; web/lider/main.ts).
# The input is the *.cs in src/ (*.ref.cs as references) and the implementation in lub's cs-lib.
# Requirements: dotnet.
# env: LUB_TCS tcs launch command (default: the .NET tcs, built by scripts/build-tcs-dotnet.sh of lub)
set -euo pipefail
[ $# -eq 1 ] || {
  echo "usage: $0 <out.tcsb>" >&2
  exit 2
}
out="$1"
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
LUB_TCS="${LUB_TCS:-$("$lub/scripts/build-tcs-dotnet.sh")}"
mapfile -t sources < <(find "$root/src" -name '*.cs' ! -name '*.ref.cs' ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/.lub/*' | sort)
mapfile -t cslib < <(find "$lub/cs-lib" -name '*.cs' ! -name lub_stub.cs | sort)
mkdir -p "$(dirname "$out")"
refs=(--ref "$lub/cs-lib/lub_stub.cs")
for r in "$root"/src/*.ref.cs; do refs+=(--ref "$r"); done
# shellcheck disable=SC2086
$LUB_TCS vm "${sources[@]}" "${cslib[@]}" "${refs[@]}" --export "Lider.*" -o "$out"
echo "wrote $out"
