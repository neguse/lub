#!/usr/bin/env bash
# release の一式 (docs/release-kit.md) を、1 つの OS と CPU の組 (target) の分だけ固める。
# target は linux-x64、linux-arm64、macos-arm64 (lub-<target>.tar.gz)、windows-x64、windows-arm64 (lub-<target>.zip)。
# 受け取るもの:
#   <build-dir>   この target の lub の build。<build-dir>/tcs に tcs と lub-gen (scripts/build-tcs-native.sh と scripts/build-lub-gen.sh の出力) がある
#   <common-dir>  OS によらない物: wasm/vm/ (web の player)、lider/Lider.tcsb (lider/scripts/build-module.sh)、dict/ (lider/scripts/build-dict.sh)
# lider の player は lider/build/player (lider/scripts/build-player.sh の出力) から取る。
# 実行ファイルは共有 library を同じディレクトリから読む (Linux は RUNPATH を $ORIGIN、macOS は rpath を @executable_path、Windows は DLL を隣に置く)。
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
mkdir -p "$kit/bin/wasm/vm" "$kit/bin/tcs/runtime" "$kit/bin/tcs/lib" "$kit/lider/data" "$kit/manual" \
  "$kit/third_party/slang" "$kit/third_party/jamo"

cp "$build/lub$exe" "$kit/bin/"
# lider の player。起動は bin/lider (Windows は bin/lider.cmd) が Lider.tcsb と lider/ の場所を渡す
cp "lider/build/player/lider_player$exe" "$kit/bin/lider-player$exe"
case "$os" in
  linux)
    # lub が名前で引く共有 library (readelf の NEEDED と同じ名前で置く)。lider の player も同じ物を引く
    cp -L "$build/deps/lib/libSDL3.so.0" \
      "$build/deps/lib/libopenxr_loader.so.1" \
      third_party/slang/lib/libslang-compiler.so.0.* "$kit/bin/"
    for f in lub lider-player; do
      patchelf --set-rpath '$ORIGIN' "$kit/bin/$f"
    done
    cp lider/kit/lider "$kit/bin/"
    ;;
  macos)
    # @rpath で引く dylib を、build の中から実行ファイルの隣に写す (写した dylib が引く物も辿る)
    pending=("$kit/bin/lub" "$kit/bin/lider-player")
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
    # build の中を指す rpath を外し、実行ファイルのディレクトリを指す
    for f in lub lider-player; do
      for p in $(otool -l "$kit/bin/$f" | awk '$1 == "cmd" && $2 == "LC_RPATH" { getline; getline; print $2 }'); do
        install_name_tool -delete_rpath "$p" "$kit/bin/$f"
      done
      install_name_tool -add_rpath @executable_path "$kit/bin/$f"
    done
    # 書き換えると署名が外れる。Apple silicon は署名の無い実行ファイルを動かさないので、ad-hoc で署名し直す
    find "$kit/bin" -maxdepth 1 -type f -exec codesign --force --sign - {} +
    cp lider/kit/lider "$kit/bin/"
    ;;
  windows)
    # DLL は実行ファイルの隣から読む。slang は slang.dll と slang-compiler.dll、DXIL の生成は dxcompiler.dll、D3D12 の runtime は Agility SDK の D3D12/
    cp "$build/SDL3.dll" "$build/slang.dll" "$build/slang-compiler.dll" "$build/dxcompiler.dll" "$kit/bin/"
    mkdir -p "$kit/bin/D3D12" "$kit/third_party/dxc" "$kit/third_party/d3d12agility"
    cp "$build/D3D12/D3D12Core.dll" "$kit/bin/D3D12/"
    for l in LLVM MIT MS; do
      cp "$build/dxc-LICENSE-$l.txt" "$kit/third_party/dxc/LICENSE-$l.txt"
    done
    cp third_party/d3d12agility/LICENSE.txt "$kit/third_party/d3d12agility/"
    cp lider/kit/lider.cmd "$kit/bin/"
    ;;
esac

# tcs VM の web の player (lub export web が埋め込む)
cp "$common/wasm/vm/lub.js" "$common/wasm/vm/lub.wasm" "$kit/bin/wasm/vm/"
# tcs は一緒に配るファイル (runtime/、lib/) を実行ファイルの dir から読む。runtime/ の C (core、math など) は tcs c が生成 C に埋め込む
cp "$build/tcs/tcs$exe" "$kit/bin/tcs/"
# lub-gen (TinyC#、scripts/build-lub-gen.sh が tcs c で C にしたもの)。lib/ を tcs と共有する
cp "$build/tcs/lub-gen$exe" "$kit/bin/tcs/"
cp -R "$build/tcs/runtime/." "$kit/bin/tcs/runtime/"
cp -R "$build/tcs/lib/." "$kit/bin/tcs/lib/"

# lider が読む file (LIDER_HOME): font と、かな漢字変換の辞書とその表示
cp "$common/lider/Lider.tcsb" "$kit/lider/"
cp lider/data/UDEVGothic-Regular.ttf lider/data/UDEVGothic-LICENSE*.txt "$kit/lider/data/"
cp "$common/dict/mozc.dict" "$common/dict/mozc-README.txt" "$common/dict/mozc-LICENSE.txt" "$kit/lider/data/"
# lider の player が静的に link するかな漢字変換 (jamo)
cp third_party/jamo/LICENSE third_party/jamo/THIRD_PARTY_NOTICES "$kit/third_party/jamo/"

cp third_party/slang/LICENSE third_party/slang/THIRD_PARTY_NOTICES.txt "$kit/third_party/slang/"
git ls-files cs-lib templates/game | while read -r f; do
  mkdir -p "$kit/$(dirname "$f")"
  cp "$f" "$kit/$f"
done
cp docs/manual/*.md "$kit/manual/"
cp tcs/doc/support-matrix.md "$kit/manual/tcs-support-matrix.md"
cp docs/release-kit.md "$kit/README.md"
cp LICENSE THIRD_PARTY_LICENSES.md "$kit/"
# サンプルは一式に入れず、この commit の GitHub から取ってもらう
git rev-parse HEAD > "$kit/COMMIT"

if [ "$os" = windows ]; then
  out="$out_dir/lub-$target.zip"
  rm -f "$out"
  # Windows の tar (bsdtar) は拡張子から zip を選ぶ
  (cd "$work" && "$(cygpath -u "$SYSTEMROOT")/System32/tar.exe" -a -cf "$(cygpath -w "$out")" lub)
else
  out="$out_dir/lub-$target.tar.gz"
  tar -czf "$out" -C "$work" lub
fi
echo "$out"
