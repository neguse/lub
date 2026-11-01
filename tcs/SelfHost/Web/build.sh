#!/bin/bash
# セルフホストの tcs compiler の WebAssembly 版を作る (Emscripten が要る。source ~/emsdk/emsdk_env.sh)。
# 出力は SelfHost/out/web/ (git に入れない):
#   tcsc-core.mjs   Lua 5.5 (LUA_32BITS) + tcs の runtime の C + tcsc_web.c (wasm を埋め込んだ 1 つの ES module)
#   tcsc.mjs        JS の入口 (createTcsc)
#   web.lua         glue (TcsSelf_Session を JSON で包む)
#   compiler.lua    compiler (SelfHost/build.sh の stage2.lua)
#   prelude.lua     runtime の前置き
#   Linq.cs         TinySystem/Linq.cs
# 先に SelfHost/build.sh で stage2.lua と prelude.lua を作っておく。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/SelfHost/out/web"
LUA="$ROOT/deps/lua"
[ -f "$ROOT/SelfHost/out/stage2.lua" ] || { echo "run SelfHost/build.sh first" >&2; exit 1; }
command -v emcc >/dev/null || { echo "emcc not found (source ~/emsdk/emsdk_env.sh)" >&2; exit 1; }
mkdir -p "$OUT"
LUA_SRC=()
for f in lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem lobject lopcodes lparser lstate lstring lstrlib ltable ltablib ltm lundump lvm lzio lauxlib lbaselib ldblib liolib lmathlib loslib loadlib lcorolib linit lutf8lib; do
  LUA_SRC+=("$LUA/$f.c")
done
emcc -O2 -DLUA_32BITS -DLUA_USE_POSIX -ffp-contract=off -fno-fast-math \
  -I "$LUA" -I "$ROOT/runtime/math" -I "$ROOT/runtime/fmt" -I "$ROOT/runtime/utf8" \
  "${LUA_SRC[@]}" "$ROOT"/runtime/math/*.c "$ROOT/runtime/fmt/f2s.c" "$ROOT/runtime/utf8/tcs_utf8.c" \
  "$ROOT/runtime/lua/tcs_math_lua.c" "$ROOT/runtime/lua/tcs_rt_lua.c" "$ROOT/runtime/lua/tcs_utf8_lua.c" \
  "$ROOT/SelfHost/Web/tcsc_web.c" \
  -s MODULARIZE=1 -s EXPORT_ES6=1 -s SINGLE_FILE=1 -s ENVIRONMENT=web,worker,node \
  -s ALLOW_MEMORY_GROWTH=1 -s STACK_SIZE=1048576 \
  -s EXPORTED_FUNCTIONS=_tcsc_load,_tcsc_call,_malloc,_free \
  -s EXPORTED_RUNTIME_METHODS=UTF8ToString,stringToUTF8,lengthBytesUTF8 \
  -o "$OUT/tcsc-core.mjs"
cp "$ROOT/SelfHost/Web/tcsc.mjs" "$ROOT/SelfHost/Web/web.lua" "$OUT/"
cp "$ROOT/SelfHost/out/stage2.lua" "$OUT/compiler.lua"
cp "$ROOT/SelfHost/out/prelude.lua" "$OUT/prelude.lua"
cp "$ROOT/TinySystem/Linq.cs" "$OUT/Linq.cs"
echo "web: $OUT"
