#!/usr/bin/env bash
# Builds the .NET assemblies that the release kit ships for .NET execution (dotnet/ of the kit) into <out-dir>.
# In the kit, templates/game/Game.csproj references these instead of the projects of the checkout.
#   - Lub.dll: the facade and host for the C API (dotnet/Lub)
#   - tcs-system.dll, with tcs-system.props and the sources it compiles into the game (Linq.cs, Collections.cs) in tcs-system/
#     (the analyzer recognizes those sources by the directory name and does not check them as game sources)
#   - tcs-analyzers.dll: the TinyC# diagnostics in dotnet build and the IDE
# They do not depend on the OS or CPU, so one build serves every target.
#
# Usage: scripts/build-dotnet-lib.sh <out-dir>
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
mkdir -p "${1:?out dir}/tcs-system"
out="$(cd "$1" && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for p in dotnet/Lub src/tcs-system src/tcs-analyzers; do
  dotnet build "$p" -c Release -nologo -v q -o "$work/$(basename "$p")"
done
cp "$work/Lub/Lub.dll" "$work/tcs-system/tcs-system.dll" "$work/tcs-analyzers/tcs-analyzers.dll" "$out/"
cp src/tcs-system/tcs-system.props src/tcs-system/Linq.cs src/tcs-system/Collections.cs "$out/tcs-system/"
