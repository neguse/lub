#!/usr/bin/env bash
# Runs the editor core (src/Editor) tests (tests/editor/<name>/) as standalone entries without running lider.
# For each test, converts the *.cs in the test dir, tests/editor/EditorTest.cs, src/Editor, src/Core/{Utf8,Keys}.cs and src/Disk.cs to tcs VM and runs them on the player.
# The player is chosen by scripts/player.sh (kana-kanji conversion is the jamo statically linked into the lider player, or src/Jamo.so loaded by lub).
# Tests write files, so they are launched with a temporary directory as cwd.
# env: PLAYER / LUB_TCS are in scripts/player.sh
#   JAMO_DICT kana-kanji conversion dictionary (default: lider's data/mozc.dict; if missing, IME tests pass without checking conversion)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/player.sh"
lub="$lub_root"
TCS="$LUB_TCS"
shared=("$root/tests/editor/EditorTest.cs" "$root"/src/Editor/*.cs "$root/src/Core/Utf8.cs" "$root/src/Core/Keys.cs" "$root/src/Disk.cs")
mapfile -t cslib < <(find "$lub/cs-lib" -name '*.cs' ! -name lub_stub.cs)
status=0
for test_dir in "$root"/tests/editor/*/; do
  name="$(basename "$test_dir")"
  entry="$(basename "$(ls "$test_dir"/Test*.cs | head -1)" .cs)"
  dir="$(mktemp -d)"
  echo "==> editor/$name"
  # shellcheck disable=SC2086
  if ! $TCS vm "$test_dir"*.cs "${shared[@]}" "${cslib[@]}" --ref "$lub/cs-lib/lub_stub.cs" --ref "$root/src/Jamo.ref.cs" --ref "$root/src/Fs.ref.cs" --export "$entry.*" -o "$dir/test.tcsb"; then
    echo "FAILED editor/$name (compile)"
    status=1
    rm -rf "$dir"
    continue
  fi
  if (cd "$dir" && EDITOR_TEST_DIR="$dir" LIDER_JAMO_USER="$dir/jamo-user.dat" LIDER_JAMO_DICT="${JAMO_DICT:-$root/data/mozc.dict}" \
    SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" timeout 120 "$player" "$dir/test.tcsb" --entry "$entry" $([ "$(basename "$player")" = lub ] && echo --extension "$root/src/Jamo.so" --extension "$root/src/Fs.so")); then
    echo "ok editor/$name (vm)"
  else
    echo "FAILED editor/$name (vm)"
    status=1
  fi
  rm -rf "$dir"
done
exit "$status"
