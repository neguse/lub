#!/usr/bin/env bash
# Step2: haxe.bc(OCaml bytecode)を生成する。
#   src/dune の haxe executable stanza に元から
#     ; (modes byte)         ← ocamldebug 用にコメントアウトされている
#   があるので、これを (modes byte exe) に変えて bytecode を出す。
# 注: dune の bare bytecode は C スタブを dll で遅延ロードするため、
#     ocamlrun で直接実行すると libz/libuv 等の symbol が未解決で落ちる。
#     これは bytecode 直接実行特有の問題で wasm_of_ocaml には無関係
#     (wsoo は .so を使わず primitive を JS/wasm で代替する)。
#     bytecode の正しさは Step6 の「wasm 出力 == native golden バイト一致」で
#     end-to-end に保証する。
set -euxo pipefail
HAXE_SRC="${HAXE_SRC:-/home/opam/haxe}"
eval "$(opam env)"
cd "$HAXE_SRC"

# enable byte mode (idempotent)
if grep -qE '^\t; \(modes byte\)' src/dune; then
  cp -n src/dune src/dune.orig || true
  sed -i 's/^\t; (modes byte)/\t(modes byte exe)/' src/dune
fi
grep -nE 'modes|name haxe' src/dune

dune build --profile release src/haxe.bc
ls -la _build/default/src/haxe.bc
echo "=== haxe.bc built ==="
