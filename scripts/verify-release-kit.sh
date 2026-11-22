#!/usr/bin/env bash
# Checks the path of writing and running the template game in a place where only the release kit (the output of scripts/package-release.sh) is extracted.
# The tcs and cs-lib of the repository are not used.
# - lub export web can turn it into a page, and the WGSL of the shaders is included
# - Even with dotnet removed from PATH, the tcs of the kit can check it, and the lub-gen of the kit can generate the glue code
# - dotnet run builds it with the .NET assemblies of the kit and runs it on the liblub of the kit (it ends after capturing a picture). The caller provides the .NET SDK
# - lider can open the template game, convert it and run it (it ends after capturing a picture). The caller provides an environment that can render (a GPU or CPU rasterizer, and a display)
#
# Usage: scripts/verify-release-kit.sh <lub-<target>.tar.gz or lub-<target>.zip>
set -euo pipefail

archive="$(cd "$(dirname "${1:?archive}")" && pwd)/$(basename "$1")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
case "$archive" in
  *.zip)
    windows=1
    (cd "$work" && "$(cygpath -u "$SYSTEMROOT")/System32/tar.exe" -xf "$(cygpath -w "$archive")")
    ;;
  *)
    windows=0
    tar xf "$archive" -C "$work"
    ;;
esac
kit="$work/lub"
test -s "$kit/manual/tcs-support-matrix.md"
test -s "$kit/bin/wasm/xr.mjs"
test -s "$kit/COMMIT"
test -s "$kit/lider/Lider.tcsb"
test -s "$kit/lider/data/mozc.dict"

cp -r "$kit/templates/game" "$work/mygame"
cd "$work/mygame"
../lub/bin/lub export web Game.csproj -o out
test -s out/index.html
grep -qa '@fragment' out/cache/shader/*.lubshader

# Even with dotnet removed from PATH, the tcs of the kit can check the same template
PATH=/usr/bin:/bin ../lub/bin/lub check Game.csproj
# The lub-gen of the kit can also read the stub without dotnet and generate the glue code of the template (as with tcs c, the inputs are the game followed by the cs-lib implementation)
gen_args=()
for f in Game.cs ../lub/cs-lib/lub/*.cs ../lub/cs-lib/lubx/*.cs; do gen_args+=(--source "$f"); done
PATH=/usr/bin:/bin ../lub/bin/tcs/lub-gen tcs --stub ../lub/cs-lib/lub_stub.cs "${gen_args[@]}" -o binding.c
test -s binding.c

# .NET execution. The template was copied out of the kit, so LubRoot points at the kit
lub_root="$kit"
[ "$windows" = 1 ] && lub_root="$(cygpath -m "$kit")"
dotnet run --property:LubRoot="$lub_root" -- --capture dotnet.png --capture-frame 30
test -s dotnet.png

# lider opens the template, converts it with the tcs built into the player, and runs it. The converted module is written to .lub/Game.tcsb (what the lub above wrote is deleted first)
rm -rf .lub
if [ "$windows" = 1 ]; then
  ../lub/bin/lider.cmd Game.csproj --capture lider.png --capture-frame 120
else
  ../lub/bin/lider Game.csproj --capture lider.png --capture-frame 120
fi
test -s lider.png
test -s .lub/Game.tcsb
echo "verified: $(basename "$archive")"
