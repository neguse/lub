#!/usr/bin/env bash
# lub export web の検査。tests/cs のテストを web に export し、生成キャッシュが
# WGSL だけを持ち、読んだ file がそのまま入ること、書いた page 1 枚 (slang を
# 持たない web の player 入り) を chromium で開くとテストが PASS を出すことを
# 見る。lub の実行ファイルの隣か build/wasm に web の player が要り、テストの
# Lua (tests/cs/.lub/) と web/node_modules (playwright) を先に用意しておく。
#
# Usage: scripts/web-export-gate.sh <lub>
set -euo pipefail

lub="${1:?usage: scripts/web-export-gate.sh <lub>}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

for t in TestVertexPull TestDepthSample TestUiRender TestDupBinding TestWindowSize; do
  echo "==> $lub export web tests/cs/.lub/$t.lua"
  env -u DISPLAY -u WAYLAND_DISPLAY "$lub" export web "tests/cs/.lub/$t.lua" -o "$out/$t"
done
for f in tests/cs/data/test_depth_sample{,_read}.{vs,fs}.slang; do
  cmp "$f" "$out/TestDepthSample/$f"
done
# TestVertexPull 1 つ、TestDepthSample 2 つ、TestUiRender (runtime の UI) 1 つ
wgsl="$(grep -la '@fragment' "$out"/Test{VertexPull,DepthSample,UiRender}/cache/shader/*.lubshader | wc -l)"
if [[ $wgsl -lt 4 ]]; then
  echo "export web: expected the WGSL of 4 shaders in the shader cache, got $wgsl" >&2
  exit 1
fi
other="$(grep -La '@fragment' "$out"/*/cache/shader/*.lubshader || true)"
if [[ -n $other ]]; then
  echo "export web: shader cache entries that are not WGSL: $other" >&2
  exit 1
fi
# PASS で終わるテストだけを page で開く (他の 2 つは golden で合否を見る)
(cd web && node scripts/export-page-check.mjs "$out/TestUiRender/index.html" \
  "$out/TestDupBinding/index.html" "$out/TestWindowSize/index.html")
