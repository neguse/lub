#!/usr/bin/env bash
# lider の player (native/CMakeLists.txt: lub の player の library に、かな漢字変換と file system の操作を静的に link したもの) を build する。
#   scripts/build-player.sh        native (build/player/lider_player)。先に lub を build しておく (scripts/build-release.sh)
#   scripts/build-player.sh --web  web (build/player-web/web/lub.{js,wasm})。先に lub の wasm を build しておく (emcmake cmake --preset wasm-release)。要 emsdk と rustup の wasm32-unknown-emscripten
# lub の build は find_package(lub) で使う (lub_DIR)。
# env: LUB_BUILD lub の build のディレクトリ (既定: native は build-release-linux、web は build/wasm)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
if [ "${1:-}" = --web ]; then
  emcmake cmake -S "$root/native" -B "$root/build/player-web" -G Ninja -Dlub_DIR="${LUB_BUILD:-$lub/build/wasm}"
  cmake --build "$root/build/player-web" --target lider_player
else
  cmake -S "$root/native" -B "$root/build/player" -G Ninja -Dlub_DIR="${LUB_BUILD:-$lub/build-release-linux}"
  cmake --build "$root/build/player" --target lider_player
fi
