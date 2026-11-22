#!/usr/bin/env bash
# Builds the lider player (native/CMakeLists.txt: the lub player library with kana-kanji conversion and file system operations statically linked in).
#   scripts/build-player.sh        native (build/player/lider_player). Build lub first (scripts/build-release.sh)
#   scripts/build-player.sh --web  web (build/player-web/web/lub.{js,wasm}). Build the lub wasm first (emcmake cmake --preset wasm-release). Requires emsdk and rustup's wasm32-unknown-emscripten
# The lub build is used through find_package(lub) (lub_DIR).
# env: LUB_BUILD lub build directory (default: build-release-linux for native, build/wasm for the web)
#      JAMO_CACHE dir for the prebuilt jamo-core (cached by CI; separate per jamo commit, Rust target and rustc)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
# If JAMO_CACHE is set, uses the prebuilt jamo-core in that dir; if there is none, puts the built one there
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
  # If the C tcs (scripts/build-tcs-native.sh) exists, uses it to convert tcs to C (no dotnet needed)
  tcs_native=""
  for f in "$lub_build/tcs/tcs" "$lub_build/tcs/tcs.exe"; do
    [ -f "$f" ] && tcs_native="$f"
  done
  cmake -S "$root/native" -B "$root/build/player" -G Ninja -Dlub_DIR="$lub_build" -DTCS_NATIVE="$tcs_native" ${jamo_args[@]+"${jamo_args[@]}"}
  cmake --build "$root/build/player" --target lider_player
  keep_jamo "$root/build/player"
fi
