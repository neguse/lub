#!/usr/bin/env bash
# OCaml の systhreads ライブラリ(threads.cma)を pure-OCaml stub に差し替える。
# wsoo は systhreads(caml_thread_self 等、Thread.t は custom block)を実装しないが、
# Haxe の eval(マクロインタプリタ)が context 生成時に Thread.self() を呼ぶ。
# Thread.t を int にした pure-OCaml stub(thread.ml/event.ml)で十分(playground の
# macro 無し compile では実スレッドを spawn しない)。元の .mli はそのまま使うので
# .cmi digest が一致し、Haxe 側の再コンパイルは不要。
set -euxo pipefail
PATCHES="${PATCHES:-$(cd "$(dirname "$0")/../patches" && pwd)}"
eval "$(opam env)"
TD="$(ocamlfind printconf destdir)/../ocaml/threads"
[ -d "$TD" ] || TD="$(dirname "$(ocamlfind query -format '%d' stdlib 2>/dev/null || echo /)")/ocaml/threads"
# 確実に解決: thread.cmi のあるディレクトリ
TD="$(dirname "$(find "$(opam var lib)/ocaml/threads" -name thread.cmi 2>/dev/null | head -1)")"

rm -rf /tmp/threads-stub; mkdir -p /tmp/threads-stub; cd /tmp/threads-stub
cp "$TD/thread.mli" "$TD/event.mli" .
cp "$PATCHES/threads-thread.ml" thread.ml
cp "$PATCHES/threads-event.ml" event.ml
ocamlfind ocamlc -package unix -c thread.mli thread.ml event.mli event.ml
ocamlfind ocamlc -a -o threads.cma thread.cmo event.cmo
cp -f "$TD/threads.cma" "$TD/threads.cma.orig" 2>/dev/null || true
cp -f thread.cmi event.cmi threads.cma "$TD/"
echo "=== threads stub overlaid into $TD ==="
