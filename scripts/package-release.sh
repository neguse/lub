#!/usr/bin/env bash
# release の一式 (docs/release-kit.md) を lub-linux-x64.tar.gz に固める。
# lub の Linux の build、wasm の build、tcs2c で C にした tcs (scripts/build-tcs-native.sh の出力の dir) を受け取る。
# lub の RUNPATH を $ORIGIN にし、共有 library を lub の隣に置く (patchelf が要る)。
#
# Usage: scripts/package-release.sh <build-dir> <wasm-dir> <tcs-dir> <out.tar.gz>
set -euo pipefail

build="${1:?build dir}"
wasm="${2:?wasm dir}"
tcs="${3:?tcs dir}"
out="$(realpath -m "${4:?out.tar.gz}")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
kit="$work/lub"
mkdir -p "$kit/bin/wasm/vm" "$kit/bin/tcs/runtime" "$kit/bin/tcs/lib" "$kit/manual" "$kit/third_party/slang"

cp "$build/lub" "$kit/bin/"
# lub が名前で引く共有 library (readelf の NEEDED と同じ名前で置く)
cp -L "$build/third_party/SDL/libSDL3.so.0" \
  "$build/third_party/openxr/src/loader/libopenxr_loader.so.1" \
  third_party/slang/lib/libslang-compiler.so.0.* "$kit/bin/"
patchelf --set-rpath '$ORIGIN' "$kit/bin/lub"

# tcs VM の web の player (lub export web が埋め込む)
cp "$wasm/vm/lub.js" "$wasm/vm/lub.wasm" "$kit/bin/wasm/vm/"
# tcs は一緒に配るファイル (runtime/、lib/) を実行ファイルの dir から読む。runtime/ の C (core、math など) は tcs c が生成 C に埋め込む
cp "$tcs/tcs" "$kit/bin/tcs/"
# lub-gen (TinyC#、scripts/build-lub-gen.sh が tcs c で C にしたもの)。lib/ を tcs と共有する
cp "$tcs/lub-gen" "$kit/bin/tcs/"
cp -R "$tcs"/runtime/. "$kit/bin/tcs/runtime/"
cp -R "$tcs"/lib/. "$kit/bin/tcs/lib/"

cp third_party/slang/LICENSE third_party/slang/THIRD_PARTY_NOTICES.txt "$kit/third_party/slang/"
git ls-files cs-lib templates/game | while read -r f; do
  mkdir -p "$kit/$(dirname "$f")"
  cp "$f" "$kit/$f"
done
cp docs/manual/*.md "$kit/manual/"
cp tcs/doc/support-matrix.md "$kit/manual/tcs-support-matrix.md"
cp docs/release-kit.md "$kit/README.md"
cp LICENSE THIRD_PARTY_LICENSES.md "$kit/"
# サンプルとフォントは一式に入れず、この commit の GitHub から取ってもらう
git rev-parse HEAD > "$kit/COMMIT"

mkdir -p "$(dirname "$out")"
tar -czf "$out" -C "$work" lub
echo "$out"
