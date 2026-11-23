#!/usr/bin/env bash
# Check of lub export web.
# Exports the tests in tests/cs to web, and checks that the generated cache holds only WGSL, that the files read are included as is, and that opening the single written page (containing the web tcs VM player without slang) in chromium makes the test print PASS.
# The web tcs VM player is needed in wasm/vm next to the lub executable or in build/wasm/vm, and the test bytecode (tests/cs/.lub/all.tcsb) and web/node_modules (playwright) must be prepared first.
#
# Usage: scripts/web-export-gate.sh <lub>
set -euo pipefail

lub="${1:?usage: scripts/web-export-gate.sh <lub>}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

for t in TestVertexPull TestDepthSample TestUiRender TestDupBinding TestWindowSize; do
  echo "==> $lub export web tests/cs/.lub/all.tcsb --entry $t"
  env -u DISPLAY -u WAYLAND_DISPLAY "$lub" export web tests/cs/.lub/all.tcsb --entry "$t" -o "$out/$t"
done
for f in tests/cs/data/test_depth_sample{,_read}.{vs,fs}.slang; do
  cmp "$f" "$out/TestDepthSample/$f"
done
# One TestVertexPull, two TestDepthSample, one TestUiRender (runtime UI)
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
# Only the tests that end with PASS are opened in the page (the other 2 are judged by goldens)
(cd web && node scripts/export-page-check.mjs "$out/TestUiRender/index.html" \
  "$out/TestDupBinding/index.html" "$out/TestWindowSize/index.html")
