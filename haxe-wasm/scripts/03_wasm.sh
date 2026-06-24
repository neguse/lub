#!/usr/bin/env bash
# Step3/5: haxe.bc を wasm_of_ocaml で wasm 化する。
#   - effects=cps: JSPI 不要で Node22+ / 全 WasmGC ブラウザで動く。
#   - binaryen は version_130+(apt の 108 は wasm-merge 欠落で wsoo runtime ビルド不可)。
#
# 重要な学び: 未解決 C primitive は wsoo 生成 glue の wasm import `env` に
#   "name":()=>{throw new Error("name not implemented")}
# として埋まる。js_of_ocaml 流の `//Provides:` JS shim は C-import 名前空間を
# 上書きしないため効かない(harness/shims.js は廃止)。
# → 対処は2系統:
#   (A) Haxe 同梱 libs(pcre2/extc 等)は OCaml ソースを pure-OCaml 化して env import
#       ごと消す(patches/haxe-5.0.0-preview.1-wasm.diff)。最もクリーン。
#   (B) opam lib 由来(integers/sha 等)は lib を pin して同様に pure-OCaml 化するか、
#       env stub を harness/patch_env.mjs で後段置換する。
set -euxo pipefail
HAXE_SRC="${HAXE_SRC:-/home/opam/haxe}"
REPO="${REPO:-/repo}"
OUT="$REPO/haxe-wasm/build/wasm"
eval "$(opam env)"
export PATH=/usr/local/bin:$PATH

# (A) Haxe-vendored libs の pure-OCaml 化パッチを適用(idempotent)
git -C "$HAXE_SRC" apply --check "$REPO/haxe-wasm/patches/haxe-5.0.0-preview.1-wasm.diff" 2>/dev/null \
  && git -C "$HAXE_SRC" apply "$REPO/haxe-wasm/patches/haxe-5.0.0-preview.1-wasm.diff" || true

# bytecode を作り直して wasm 化
cd "$HAXE_SRC"
dune build --profile release src/haxe.bc
mkdir -p "$OUT"
wasm_of_ocaml compile --effects=cps _build/default/src/haxe.bc -o "$OUT/haxe.js" \
  2> "$REPO/haxe-wasm/build/wasm_compile.stderr"
echo "=== linker-missing primitives(全体像。実呼出は results/actually_called_primitives.txt)==="
awk '/Missing primitives:/{f=1} f' "$REPO/haxe-wasm/build/wasm_compile.stderr" | head -5 || true
ls -la "$OUT"
