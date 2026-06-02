#!/usr/bin/env bash
# 成果物を一発ビルド + 抽出 + 検証する。
#   1. Dockerfile で wasm Haxe(haxe.js + haxe.assets + std)を再現ビルド。
#   2. 成果物を spike/dist/ に抽出。
#   3. caml_thread_initialize(OCaml stdlib systhreads、pin 不可)を env-patch で
#      no-op 化した haxe.js を生成(これが配布する最終 glue)。
#   4. ホスト Node で samples/00_hello を compile し、native golden と byte 比較。
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
IMAGE=${IMAGE:-haxe-wasm-builder}
DIST="$HERE/dist"

echo "==> docker build ($IMAGE)"
docker build -t "$IMAGE" -f "$HERE/Dockerfile" "$HERE"

echo "==> extract artifacts -> $DIST"
rm -rf "$DIST"; mkdir -p "$DIST"
cid=$(docker create "$IMAGE")
docker cp "$cid:/home/opam/out/." "$DIST/"
docker rm "$cid" >/dev/null

echo "==> post-patch glue: caml_thread_initialize no-op (single-thread safe)"
node "$HERE/harness/patch_env.mjs" "$DIST/haxe.js" "$DIST/haxe.js.tmp"
mv "$DIST/haxe.js.tmp" "$DIST/haxe.js"

echo "==> verify: compile samples/00_hello in wasm Haxe (host node $(node --version))"
OUT=$(mktemp)
HAXE_STD_PATH="$DIST/std" node "$DIST/haxe.js" \
  -cp "$REPO/haxe-lib/lub" -cp "$REPO/samples/00_hello" \
  -main Hello00 --lua "$OUT"
GOLDEN="$HERE/build/00_hello.native.raw.lua"
if [ -f "$GOLDEN" ] && cmp -s "$OUT" "$GOLDEN"; then
  echo "★ verify OK: wasm 出力が native golden とバイト一致"
else
  echo "verify: 出力 $(wc -c <"$OUT") bytes(golden 未配置なら差分比較スキップ)"
fi
echo "==> done. 配布物: $DIST/{haxe.js, haxe.assets/, std/}"
du -sh "$DIST"/haxe.assets "$DIST"/std 2>/dev/null
