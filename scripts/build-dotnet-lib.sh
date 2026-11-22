#!/usr/bin/env bash
# Builds the .NET assemblies that the release kit ships for .NET execution (dotnet/ of the kit) into <out-dir>.
# In the kit, templates/game/Game.csproj references these instead of the projects of the checkout.
#   - Lub.dll: the facade and host for the C API (dotnet/Lub)
#   - TinySystem.dll, with TinySystem.props and the sources it compiles into the game (Linq.cs, Collections.cs) in TinySystem/
#     (the analyzer recognizes those sources by the directory name TinySystem and does not check them as game sources)
#   - TinyCs.Analyzers.dll: the TinyC# diagnostics in dotnet build and the IDE
# They do not depend on the OS or CPU, so one build serves every target.
#
# Usage: scripts/build-dotnet-lib.sh <out-dir>
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
mkdir -p "${1:?out dir}/TinySystem"
out="$(cd "$1" && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for p in dotnet/Lub tcs/TinySystem tcs/TinyCs.Analyzers; do
  dotnet build "$p" -c Release -nologo -v q -o "$work/$(basename "$p")"
done
cp "$work/Lub/Lub.dll" "$work/TinySystem/TinySystem.dll" "$work/TinyCs.Analyzers/TinyCs.Analyzers.dll" "$out/"
cp tcs/TinySystem/TinySystem.props tcs/TinySystem/Linq.cs tcs/TinySystem/Collections.cs "$out/TinySystem/"
