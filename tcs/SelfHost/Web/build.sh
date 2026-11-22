#!/bin/bash
# Builds the WebAssembly version of tcs (requires dotnet and Emscripten; source ~/emsdk/emsdk_env.sh).
# tcs2c turns tcs (SelfHost/Compiler, SelfHost/CBackend, SelfHost/VmBackend, SelfHost/Tool) into C, and emcc builds it into wasm together with tcs_web.c (host).
# Output goes to SelfHost/out/web/ (not checked into git):
#   tcs-core.mjs    a single ES module with the wasm embedded
#   tcs.mjs         JS entry point (createTcs)
#   lib/            files shipped with tcs (JS passes them through the toolFiles of createTcs)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/SelfHost/out/web"
command -v emcc >/dev/null || { echo "emcc not found (source ~/emsdk/emsdk_env.sh)" >&2; exit 1; }
rm -rf "$OUT"
mkdir -p "$OUT/lib/TinySystem"
dotnet build "$ROOT/tcs2c" -c Release -v q -nologo >/dev/null
cd "$ROOT"
SRC=(SelfHost/Compiler/*.cs SelfHost/CBackend/*.cs SelfHost/VmBackend/*.cs)
for f in SelfHost/Tool/*.cs; do
  [ "$(basename "$f")" = TcsHost.cs ] || SRC+=("$f")
done
dotnet tcs2c/bin/Release/net11.0/tcs2c.dll --lib --ref SelfHost/Tool/TcsHost.cs "${SRC[@]}" -o "$OUT/tcs.c"
emcc -O2 -ffp-contract=off -fwrapv -I "$OUT" SelfHost/Web/tcs_web.c \
  -s MODULARIZE=1 -s EXPORT_ES6=1 -s SINGLE_FILE=1 -s ENVIRONMENT=web,worker,node \
  -s ALLOW_MEMORY_GROWTH=1 -s STACK_SIZE=8388608 \
  -s EXPORTED_FUNCTIONS=_tcs_web_init,_tcs_web_clear_args,_tcs_web_push_arg,_tcs_web_tool_file,_tcs_web_call,_tcs_web_out_length,_malloc,_free \
  -s EXPORTED_RUNTIME_METHODS=HEAPU8 \
  -o "$OUT/tcs-core.mjs"
rm -f "$OUT/tcs.c"
cp SelfHost/Web/tcs.mjs "$OUT/"
cp TinySystem/Linq.cs TinySystem/ValueTuple.cs "$OUT/lib/TinySystem/"
echo "web: $OUT"
