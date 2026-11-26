#!/usr/bin/env bash
# Packs the release archive for one OS and CPU pair (target).
# Targets are linux-x64, linux-arm64, macos-arm64 (lub-<target>.tar.gz), windows-x64, windows-arm64 (lub-<target>.zip).
# Inputs:
#   <build-dir>   the lub build for this target. <build-dir>/tcs holds tcs and lub-gen (the output of scripts/build-tcs-native.sh and scripts/build-lub-gen.sh)
#   <common-dir>  OS-independent files: wasm/vm/ (the web player), lider/Lider.tcsb (lider/scripts/build-module.sh), dict/ (lider/scripts/build-dict.sh),
#                 dotnet/ (the .NET assemblies for .NET execution, scripts/build-dotnet-lib.sh)
# The lider player is taken from lider/build/player (the output of lider/scripts/build-player.sh).
# Executables and the shared library of lub (liblub, used by .NET execution) load shared libraries from the same directory
# (RUNPATH is $ORIGIN on Linux, rpath is @executable_path / @loader_path on macOS, and DLLs are placed next to them on Windows).
#
# Usage: scripts/package-release.sh <target> <build-dir> <common-dir> <out-dir>
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

target="${1:?target}"
build="$(cd "${2:?build dir}" && pwd)"
common="$(cd "${3:?common dir}" && pwd)"
mkdir -p "${4:?out dir}"
out_dir="$(cd "$4" && pwd)"
case "$target" in
  linux-x64 | linux-arm64) os=linux exe="" ;;
  macos-arm64) os=macos exe="" ;;
  windows-x64 | windows-arm64) os=windows exe=".exe" ;;
  *)
    echo "unknown target: $target" >&2
    exit 2
    ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
kit="$work/lub"
mkdir -p "$kit/bin/wasm/vm" "$kit/bin/tcs/runtime" "$kit/bin/tcs/lib" "$kit/lider/data" "$kit/manual" "$kit/dotnet" \
  "$kit/third_party/slang" "$kit/third_party/jamo"

cp "$build/lub$exe" "$kit/bin/"
# The lider player. bin/lider (bin/lider.cmd on Windows) launches it and passes the locations of Lider.tcsb and lider/
cp "lider/build/player/lider_player$exe" "$kit/bin/lider-player$exe"
case "$os" in
  linux)
    # Shared libraries that lub looks up by name (placed under the same names as NEEDED in readelf). The lider player and liblub look up the same ones
    cp -L "$build/deps/lib/libSDL3.so.0" \
      "$build/deps/lib/libopenxr_loader.so.1" \
      third_party/slang/lib/libslang-compiler.so.0.* "$build/liblub.so" "$kit/bin/"
    for f in lub lider-player liblub.so; do
      patchelf --set-rpath '$ORIGIN' "$kit/bin/$f"
    done
    cp lider/kit/lider "$kit/bin/"
    ;;
  macos)
    cp "$build/liblub.dylib" "$kit/bin/"
    chmod u+w "$kit/bin/liblub.dylib"
    # Copy the dylibs looked up via @rpath from inside the build to next to the executable (also following what the copied dylibs look up)
    pending=("$kit/bin/lub" "$kit/bin/lider-player" "$kit/bin/liblub.dylib")
    while [ ${#pending[@]} -gt 0 ]; do
      f="${pending[0]}"
      pending=("${pending[@]:1}")
      for name in $(otool -L "$f" | awk 'NR > 1 && $1 ~ /^@rpath\// { sub("^@rpath/", "", $1); print $1 }'); do
        [ -e "$kit/bin/$name" ] && continue
        src="$(find "$build/deps/lib" third_party/slang/lib -maxdepth 1 -name "$name" | head -1)"
        if [ -z "$src" ]; then
          echo "$f needs @rpath/$name, which is not in the build" >&2
          exit 1
        fi
        cp -L "$src" "$kit/bin/$name"
        chmod u+w "$kit/bin/$name"
        pending+=("$kit/bin/$name")
      done
    done
    # Remove the rpath that points into the build, and point it at the directory of the executable
    for f in lub lider-player; do
      for p in $(otool -l "$kit/bin/$f" | awk '$1 == "cmd" && $2 == "LC_RPATH" { getline; getline; print $2 }'); do
        install_name_tool -delete_rpath "$p" "$kit/bin/$f"
      done
      install_name_tool -add_rpath @executable_path "$kit/bin/$f"
    done
    # liblub is loaded by dotnet, so it looks next to itself (@loader_path), not next to the executable
    for p in $(otool -l "$kit/bin/liblub.dylib" | awk '$1 == "cmd" && $2 == "LC_RPATH" { getline; getline; print $2 }'); do
      install_name_tool -delete_rpath "$p" "$kit/bin/liblub.dylib"
    done
    install_name_tool -add_rpath @loader_path "$kit/bin/liblub.dylib"
    # Rewriting removes the signature. Apple silicon does not run unsigned executables, so re-sign ad-hoc
    find "$kit/bin" -maxdepth 1 -type f -exec codesign --force --sign - {} +
    cp lider/kit/lider "$kit/bin/"
    ;;
  windows)
    # DLLs are loaded from next to the executable. slang is slang.dll and slang-compiler.dll, DXIL generation is dxcompiler.dll, and the D3D12 runtime is D3D12/ of the Agility SDK
    cp "$build/lub.dll" "$build/SDL3.dll" "$build/slang.dll" "$build/slang-compiler.dll" "$build/dxcompiler.dll" "$kit/bin/"
    mkdir -p "$kit/bin/D3D12" "$kit/third_party/dxc" "$kit/third_party/d3d12agility"
    cp "$build/D3D12/D3D12Core.dll" "$kit/bin/D3D12/"
    for l in LLVM MIT MS; do
      cp "$build/dxc-LICENSE-$l.txt" "$kit/third_party/dxc/LICENSE-$l.txt"
    done
    cp third_party/d3d12agility/LICENSE.txt "$kit/third_party/d3d12agility/"
    cp lider/kit/lider.cmd "$kit/bin/"
    ;;
esac

# The tcs VM web player (embedded by lub export web)
cp "$common/wasm/vm/lub.js" "$common/wasm/vm/lub.wasm" "$kit/bin/wasm/vm/"
# createXR for using WebXR on your own page (docs/manual/12-xr.md). Shipped as a pair with the player, from the same commit
cp web/xr.mjs "$kit/bin/wasm/"
# tcs reads the files shipped with it (runtime/, lib/) from the dir of the executable. tcs c embeds the C in runtime/ (core, math, etc.) into the generated C
cp "$build/tcs/tcs$exe" "$kit/bin/tcs/"
# lub-gen (TinyC#, converted to C with tcs c by scripts/build-lub-gen.sh). Shares lib/ with tcs
cp "$build/tcs/lub-gen$exe" "$kit/bin/tcs/"
cp -R "$build/tcs/runtime/." "$kit/bin/tcs/runtime/"
cp -R "$build/tcs/lib/." "$kit/bin/tcs/lib/"
# .NET execution of templates/game: the prebuilt assemblies it references in the kit, and the sources tcs-system.props compiles into the game
cp -R "$common/dotnet/." "$kit/dotnet/"

# Files read by lider (LIDER_HOME): fonts, and the kana-kanji conversion dictionary and its display
cp "$common/lider/Lider.tcsb" "$kit/lider/"
cp lider/data/UDEVGothic-Regular.ttf lider/data/UDEVGothic-LICENSE*.txt "$kit/lider/data/"
cp "$common/dict/mozc.dict" "$common/dict/mozc-README.txt" "$common/dict/mozc-LICENSE.txt" "$kit/lider/data/"
# Kana-kanji conversion statically linked into the lider player (jamo)
cp third_party/jamo/LICENSE third_party/jamo/THIRD_PARTY_NOTICES "$kit/third_party/jamo/"

cp third_party/slang/LICENSE third_party/slang/THIRD_PARTY_NOTICES.txt "$kit/third_party/slang/"
git ls-files cs-lib templates/game | while read -r f; do
  mkdir -p "$kit/$(dirname "$f")"
  cp "$f" "$kit/$f"
done
cp docs/manual/*.md "$kit/manual/"
cp docs/tcs/support-matrix.md "$kit/manual/tcs-support-matrix.md"
cp LICENSE THIRD_PARTY_LICENSES.md "$kit/"
# Samples are not included in the kit; users get them from GitHub at this commit
git rev-parse HEAD > "$kit/COMMIT"

if [ "$os" = windows ]; then
  out="$out_dir/lub-$target.zip"
  rm -f "$out"
  # The Windows tar (bsdtar) picks zip from the extension
  (cd "$work" && "$(cygpath -u "$SYSTEMROOT")/System32/tar.exe" -a -cf "$(cygpath -w "$out")" lub)
else
  out="$out_dir/lub-$target.tar.gz"
  tar -czf "$out" -C "$work" lub
fi
echo "$out"
