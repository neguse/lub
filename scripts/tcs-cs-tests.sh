#!/usr/bin/env bash
# Runs tcs over all of tests/cs: every *.cs in tests/cs and the cs-lib implementation sources (same convention as the lub CLI).
#   scripts/tcs-cs-tests.sh vm     compiles them into one tcs VM module, tests/cs/.lub/all.tcsb. lub runs a test with --entry <Entry>
#   scripts/tcs-cs-tests.sh check  tcs check (fails on warnings too; tcs vm does not stop on them)
# The tcs is scripts/tcs.sh (LUB_TCS if set, otherwise the .NET tcs).
set -euo pipefail
cd "$(dirname "$0")/.."
mode="${1:?usage: tcs-cs-tests.sh vm|check}"
mapfile -t sources < <(
  find tests/cs \( -name bin -o -name obj -o -name '.?*' \) -prune -o -name '*.cs' -print
  find cs-lib -name '*.cs' ! -name 'lub_stub.cs'
)
case "$mode" in
  vm)
    exports=()
    for csproj in tests/cs/*.csproj; do
      exports+=(--export "$(basename "$csproj" .csproj).*")
    done
    mkdir -p tests/cs/.lub
    scripts/tcs.sh vm "${sources[@]}" --ref cs-lib/lub_stub.cs "${exports[@]}" -o tests/cs/.lub/all.tcsb
    ;;
  check)
    scripts/tcs.sh check "${sources[@]}" --ref cs-lib/lub_stub.cs
    ;;
  *)
    echo "usage: tcs-cs-tests.sh vm|check" >&2
    exit 2
    ;;
esac
