#!/usr/bin/env bash
# lider の player (native/CMakeLists.txt: lub の player の library に、かな漢字変換と file system の操作を静的に link したもの) を build する。
#   scripts/build-player.sh        native (build/player/lider_player)。先に lub を build しておく (scripts/build-release.sh)
#   scripts/build-player.sh --web  web (build/player-web/web/lub.{js,wasm})。先に lub の wasm を build しておく (emcmake cmake --preset wasm-release)。要 emsdk と rustup の wasm32-unknown-emscripten
# lub の build は find_package(lub) で使う (lub_DIR)。
# env: LUB_BUILD lub の build のディレクトリ (既定: native は build-release-linux、web は build/wasm)
#      JAMO_CACHE build 済みの jamo-core を置く dir (CI が cache する。jamo の commit、Rust の target、rustc ごとに分ける)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
# JAMO_CACHE があれば、その dir の build 済みの jamo-core を使い、無ければ build した物をそこに置く
jamo_args=()
jamo_cached=""
if [ -n "${JAMO_CACHE:-}" ]; then
  for f in "$JAMO_CACHE/libjamo_core.a" "$JAMO_CACHE/jamo_core.lib"; do
    [ -f "$f" ] && jamo_cached="$f"
  done
  [ -z "$jamo_cached" ] || jamo_args=(-DJAMO_PREBUILT="$jamo_cached")
fi
keep_jamo() {
  [ -n "${JAMO_CACHE:-}" ] && [ -z "$jamo_cached" ] || return 0
  mkdir -p "$JAMO_CACHE"
  find "$1/jamo" -path '*/release/*' \( -name libjamo_core.a -o -name jamo_core.lib \) -exec cp {} "$JAMO_CACHE/" \;
}
if [ "${1:-}" = --web ]; then
  emcmake cmake -S "$root/native" -B "$root/build/player-web" -G Ninja -Dlub_DIR="${LUB_BUILD:-$lub/build/wasm}" ${jamo_args[@]+"${jamo_args[@]}"}
  cmake --build "$root/build/player-web" --target lider_player
  keep_jamo "$root/build/player-web"
else
  lub_build="${LUB_BUILD:-$lub/build-release-linux}"
  # C の tcs (scripts/build-tcs-native.sh) があれば、tcs を C にするのに使う (dotnet が要らない)
  tcs_native=""
  for f in "$lub_build/tcs/tcs" "$lub_build/tcs/tcs.exe"; do
    [ -f "$f" ] && tcs_native="$f"
  done
  cmake -S "$root/native" -B "$root/build/player" -G Ninja -Dlub_DIR="$lub_build" -DTCS_NATIVE="$tcs_native" ${jamo_args[@]+"${jamo_args[@]}"}
  cmake --build "$root/build/player" --target lider_player
  keep_jamo "$root/build/player"
fi
