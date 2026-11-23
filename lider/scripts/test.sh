#!/usr/bin/env bash
# Runs lider's runtime tests (tests/<name>/) on the tcs VM player (scripts/player.sh).
# A test entry pulls in the lider sources and runs lider itself. If tests/<name>/game/Game.csproj exists, lider opens it as the game.
# Tests rewrite files, so they are copied to a temporary directory and launched there.
# If test names are given as arguments, only those run.
# env: PLAYER / LUB_TCS are in scripts/player.sh
#   JAMO_DICT  kana-kanji conversion dictionary (default: lider's data/mozc.dict). If missing, tests/ime is skipped
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/player.sh"
dict="${JAMO_DICT:-}"
if [ -z "$dict" ]; then
  if [ -f "$root/data/mozc.dict" ]; then
    dict="$root/data/mozc.dict"
  fi
fi
status=0
dirs=()
if [ $# -gt 0 ]; then
  for name in "$@"; do dirs+=("$root/tests/$name/"); done
else
  for test_dir in "$root"/tests/*/; do
    # tests/editor holds tests that do not run lider (scripts/test-editor.sh)
    [ "$(basename "$test_dir")" = editor ] && continue
    dirs+=("$test_dir")
  done
fi
for test_dir in "${dirs[@]}"; do
  name="$(basename "$test_dir")"
  # Kana-kanji conversion tests need the dictionary
  if [ "$name" = ime ] && [ -z "$dict" ]; then
    echo "skip $name (no dictionary: data/mozc.dict)"
    continue
  fi
  dir="$(mktemp -d)"
  cp -r "$test_dir." "$dir/"
  # References point to the lider directory (the csproj of the app the test opens is one dir below)
  find "$dir" -maxdepth 2 -name '*.csproj' -exec sed -i -E "s#(\.\./)+src/Lider\.csproj#$root/src/Lider.csproj#" {} +
  entry="$(basename "$(ls "$dir"/*.csproj | head -1)")"
  # The game lider opens (if any)
  game=""
  [ -f "$dir/game/Game.csproj" ] && game="$dir/game/Game.csproj"
  # Files the test opens in a window and rewrites. Put them in game/ to include them in the game build
  target="$dir/Target.cs"
  [ -f "$dir/game/Target.cs" ] && target="$dir/game/Target.cs"
  echo "==> $name"
  if (cd "$dir" && LIDER_ROOT="$dir" LIDER_GAME="$game" LIDER_HOME="$root" LIDER_ACTIVE=1 LIDER_JAMO_DICT="$dict" \
    LIDER_TEST_FILE="$target" LIDER_JAMO_USER="$dir/jamo-user.dat" LIDER_KEYS="$dir/keys.txt" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
    timeout 180 "$player" "$entry"); then
    echo "ok $name"
  else
    echo "FAILED $name"
    status=1
  fi
  rm -rf "$dir"
done
exit "$status"
