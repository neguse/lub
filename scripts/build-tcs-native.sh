#!/usr/bin/env bash
# Builds tcs converted to C with `tcs c` (src/tcs/build-native.sh) and places it in <build-dir>/tcs.
# lub first uses tcs/tcs next to its executable (src/tcs_build.c). tcs reads the files shipped with it (runtime/, lib/) from the same dir.
# The tests tcs.native.* (tests/lub-test) check that the built tcs produces the same output as the .NET tcs. Building it requires the dotnet SDK and a C compiler. Using it does not.
#
# Usage: scripts/build-tcs-native.sh [build-dir]   (default build-release-linux)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
build="${1:-build-release-linux}"
native=src/tcs/out/native
out="$build/tcs"

src/tcs/build-native.sh "$native"
rm -rf "$out"
mkdir -p "$out"
for f in tcs tcs.exe; do
  [[ -f "$native/$f" ]] && cp "$native/$f" "$out/"
done
cp -R "$native/runtime" "$native/lib" "$out/"
echo "tcs: $out/tcs"
