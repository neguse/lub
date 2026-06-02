#!/usr/bin/env bash
# Step1: Haxe 5 (5.0.0-preview.1) を OCaml5 上で native ビルドし、
#        samples/00_hello を `--lua` で raw 出力 → Haxe5 native golden 化する。
# 実行環境: ocaml/opam:debian-12-ocaml-5.2 コンテナ内、/repo に本リポを mount。
# parallelism は opt-in なので付けない(単一 domain で wasm_of_ocaml 互換を上げる)。
set -euxo pipefail

HAXE_TAG="${HAXE_TAG:-5.0.0-preview.1}"
HAXE_SRC="${HAXE_SRC:-/home/opam/haxe}"
REPO="${REPO:-/repo}"
OUT="$REPO/spike/build"
mkdir -p "$OUT"

eval "$(opam env)"

# 0. Haxe 同梱 libs/ が要求する system 開発ヘッダ。
#    libmbedtls-dev は opam 依存に出てこない(libs/mbedtls は Haxe 同梱で
#    system mbedtls ヘッダを直接 #include する)ため depext では拾われない。
#    pcre2/zlib は conf-* の depext が apt 導入するが念のため明示。
sudo apt-get update
sudo apt-get install -y libmbedtls-dev libpcre2-dev zlib1g-dev m4 pkg-config

# 1. clone haxe at the preview tag (submodules 含む)
if [ ! -d "$HAXE_SRC/.git" ]; then
  git clone --recursive https://github.com/HaxeFoundation/haxe.git "$HAXE_SRC"
fi
cd "$HAXE_SRC"
git fetch --tags origin || true
git checkout "$HAXE_TAG"
git submodule update --init --recursive

# 2. opam 依存 (depext = apt の system lib も opam が sudo で導入)。
#    parallelism build-dep が入っても native ビルドには無害。
opam install . --deps-only --yes --confirm-level=unsafe-yes

# 3. native compiler + std + haxelib をビルド
make -j"$(nproc)"
make haxelib || make tools || true
ls -la ./haxe ./haxelib 2>/dev/null || true
./haxe --version

# 4. lub externs を haxelib dev で配線
export HAXELIB_PATH=/home/opam/haxelib
mkdir -p "$HAXELIB_PATH"
./haxelib setup "$HAXELIB_PATH" || true
./haxelib dev lub "$REPO/haxe-lib/lub"

# 5. native golden compile (00_hello)
export HAXE_STD_PATH="$HAXE_SRC/std"
cd "$REPO"
"$HAXE_SRC/haxe" -cp samples/00_hello -lib lub -main Hello00 --lua "$OUT/00_hello.native.raw.lua"
wc -l "$OUT/00_hello.native.raw.lua"
echo "=== native golden written: $OUT/00_hello.native.raw.lua ==="
