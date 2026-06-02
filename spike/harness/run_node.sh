#!/usr/bin/env bash
# Step4 harness: wasm_of_ocaml 化した haxe を「ホスト Node」で実行し、
# samples/00_hello を `--lua` で compile して native golden と diff する。
#
# js_of_ocaml/wsoo は Node 実行時に実ファイルシステム(fs module)を使うため、
# 疑似FS 不要で std/externs/sample を実 fs から直接読める(疑似FSは Step6 ブラウザ用)。
# effects=cps でビルドしてあるので JSPI 不要、Node22+ で動く(host は Node26)。
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SPIKE=$(cd "$HERE/.." && pwd)
REPO=$(cd "$SPIKE/.." && pwd)

HAXE_JS="$SPIKE/build/wasm/haxe.js"
STD="$SPIKE/build/std"
OUT="$SPIKE/build/00_hello.wasm.raw.lua"
GOLDEN="$SPIKE/build/00_hello.native.raw.lua"

rm -f "$OUT"
echo "=== run wasm haxe under node ($(node --version)) ==="
set -x
HAXE_STD_PATH="$STD" node "$HAXE_JS" \
  -cp "$REPO/haxe-lib/lub" -cp "$REPO/samples/00_hello" \
  -main Hello00 --lua "$OUT"
rc=$?
set +x
echo "=== node exit: $rc ==="
if [ -f "$OUT" ]; then
  echo "=== output produced: $(wc -c < "$OUT") bytes ==="
  if cmp -s "$OUT" "$GOLDEN"; then
    echo "WASM == NATIVE GOLDEN: IDENTICAL ✓"
  else
    echo "DIFFERS from golden:"
    diff "$GOLDEN" "$OUT" | head -40
  fi
else
  echo "no output file produced"
fi
exit $rc
