#!/usr/bin/env bash
# opam lib(sha / integers / ctypes / luv)を pure-OCaml 化して pin する。
# Haxe-vendored ではないため `opam source` で取得 → patch → `opam pin --no-action`。
# 以降の `opam install . --deps-only` がこれら patched 版を使う。
# patches/ の各ファイルを使う。Dockerfile と spike/scripts/04 から共用。
set -euxo pipefail
PATCHES="${PATCHES:-$(cd "$(dirname "$0")/../patches" && pwd)}"
eval "$(opam env)"
cd /tmp

opam source sha       --dir=/tmp/sha-src
opam source integers  --dir=/tmp/integers-src
opam source ctypes    --dir=/tmp/ctypes-src
opam source luv       --dir=/tmp/luv-src

# sha: SHA1 を pure-OCaml に。.mli の external も val 化(呼出側が C 直参照を避ける)。
cp "$PATCHES/sha1.ml" /tmp/sha-src/sha1.ml
sed -i -E 's/^external (.*) = "stub_sha1_[a-z_]*"/val \1/' /tmp/sha-src/sha1.mli

# integers: UInt32/UInt64 と size/init を Int32/Int64 ベースの pure-OCaml に。
cp "$PATCHES/unsigned.ml" /tmp/integers-src/src/unsigned.ml

# ctypes: PosixTypes の typeof_* を C 非依存の arithmetic バリアント定数に。
cp "$PATCHES/ctypes-posixTypes.ml" /tmp/ctypes-src/src/ctypes/posixTypes.ml
# ctypes: メモリ primitive(allocate/block_address/write/memcpy)を pure-OCaml ダミーに
# (luv の libuv ハンドル確保が実行時に読まれないため)。
cp "$PATCHES/ctypes-memory_stubs.ml" /tmp/ctypes-src/src/ctypes/ctypes_memory_stubs.ml

# luv: module-init の version suffix 取得(C 呼出)を定数に。
cp "$PATCHES/luv-version.ml" /tmp/luv-src/src/version.ml

opam pin add -y --no-action sha       /tmp/sha-src
opam pin add -y --no-action integers  /tmp/integers-src
opam pin add -y --no-action ctypes    /tmp/ctypes-src
opam pin add -y --no-action luv       /tmp/luv-src
echo "=== pinned ==="; opam pin list | grep -E "sha|integers|ctypes|luv"
