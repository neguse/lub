#!/usr/bin/env bash
# release の一式 (docs/release-kit.md) を lub-linux-x64.tar.gz に固める。
# lub の Linux の build、wasm の build、NativeAOT で publish した tcs を受け取る。
# lub の RUNPATH を $ORIGIN にし、共有 library を lub の隣に置く (patchelf が要る)。
#
# Usage: scripts/package-release.sh <build-dir> <wasm-dir> <tcs-publish-dir> <out.tar.gz>
set -euo pipefail

build="${1:?build dir}"
wasm="${2:?wasm dir}"
tcs="${3:?tcs publish dir}"
out="$(realpath -m "${4:?out.tar.gz}")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
kit="$work/lub"
mkdir -p "$kit/bin/wasm" "$kit/bin/tcs/runtime" "$kit/samples" "$kit/third_party/lume" \
  "$kit/manual" "$kit/third_party/slang"

cp "$build/lub" "$kit/bin/"
# lub が名前で引く共有 library (readelf の NEEDED と同じ名前で置く)
cp -L "$build/third_party/SDL/libSDL3.so.0" \
  "$build/third_party/openxr/src/loader/libopenxr_loader.so.1" \
  third_party/slang/lib/libslang-compiler.so.0.* "$kit/bin/"
patchelf --set-rpath '$ORIGIN' "$kit/bin/lub"

cp "$wasm/lub.js" "$wasm/lub.wasm" "$wasm/lub.data" "$kit/bin/wasm/"
cp "$tcs/Transpiler" "$kit/bin/tcs/"
cp "$tcs/runtime/"*.lua "$kit/bin/tcs/runtime/"

cp samples/boot.lua "$kit/samples/"
cp third_party/lume/lume.lua third_party/lume/LICENSE "$kit/third_party/lume/"
cp third_party/slang/LICENSE third_party/slang/THIRD_PARTY_NOTICES.txt "$kit/third_party/slang/"
git ls-files cs-lib templates/game | while read -r f; do
  mkdir -p "$kit/$(dirname "$f")"
  cp "$f" "$kit/$f"
done
cp docs/manual/*.md "$kit/manual/"
cp third_party/tcs/doc/support-matrix.md "$kit/manual/tcs-support-matrix.md"
cp docs/release-kit.md "$kit/README.md"
cp LICENSE THIRD_PARTY_LICENSES.md "$kit/"
# サンプルとフォントは一式に入れず、この commit の GitHub から取ってもらう
git rev-parse HEAD > "$kit/COMMIT"

mkdir -p "$(dirname "$out")"
tar -czf "$out" -C "$work" lub
echo "$out"
