#!/usr/bin/env bash
# Step5 高速反復: env スタブを patch_env.mjs で実装置換 → ホスト Node で 00_hello を
# compile → golden と diff。呼ばれた missing primitive を1つずつ潰すための内ループ。
set -uo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SPIKE=$(cd "$HERE/.." && pwd)
REPO=$(cd "$SPIKE/.." && pwd)
OUT="$SPIKE/build/00_hello.wasm.raw.lua"
GOLDEN="$SPIKE/build/00_hello.native.raw.lua"

node "$HERE/patch_env.mjs" "$SPIKE/build/wasm/haxe.js" "$SPIKE/build/wasm/haxe.patched.js"
rm -f "$OUT"
HAXE_STD_PATH="$SPIKE/build/std" node "$SPIKE/build/wasm/haxe.patched.js" \
  -cp "$REPO/haxe-lib/lub" -cp "$REPO/samples/00_hello" \
  -main Hello00 --lua "$OUT"
rc=$?
echo "=== node exit: $rc ==="
if [ -f "$OUT" ]; then
  echo "output: $(wc -c < "$OUT") bytes"
  if cmp -s "$OUT" "$GOLDEN"; then echo "WASM == NATIVE GOLDEN: IDENTICAL ✓"
  else echo "DIFFERS:"; diff "$GOLDEN" "$OUT" | head -30; fi
fi
