#!/usr/bin/env bash
# lider の runtime テスト (tests/<name>/) を tcs VM の player (scripts/player.sh) で走らせる。
# test の entry は lider のソースを取り込み、lider を自分で動かす。tests/<name>/game/Game.csproj があれば、lider がそれをゲームとして開く。
# test は file を書き換えるので、一時ディレクトリに写して起動する。
# 引数に test の名前を並べると、それだけを走らせる。
# env: PLAYER / LUB_TCS は scripts/player.sh
#   JAMO_DICT  かな漢字変換の辞書 (既定: lider の data/mozc.dict)。無ければ tests/ime は飛ばす
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
    # tests/editor は lider を動かさない test (scripts/test-editor.sh)
    [ "$(basename "$test_dir")" = editor ] && continue
    dirs+=("$test_dir")
  done
fi
for test_dir in "${dirs[@]}"; do
  name="$(basename "$test_dir")"
  # かな漢字変換の test は辞書が要る
  if [ "$name" = ime ] && [ -z "$dict" ]; then
    echo "skip $name (no dictionary: data/mozc.dict)"
    continue
  fi
  dir="$(mktemp -d)"
  cp -r "$test_dir." "$dir/"
  # 参照は lider のディレクトリを指す (test が開く app の csproj は 1 つ下の dir にある)
  find "$dir" -maxdepth 2 -name '*.csproj' -exec sed -i -E "s#(\.\./)+src/Lider\.csproj#$root/src/Lider.csproj#" {} +
  entry="$(basename "$(ls "$dir"/*.csproj | head -1)")"
  # lider が開くゲーム (あれば)
  game=""
  [ -f "$dir/game/Game.csproj" ] && game="$dir/game/Game.csproj"
  # test が窓で開いて書き換える file。ゲームの build に入れるなら game/ に置く
  target="$dir/Target.cs"
  [ -f "$dir/game/Target.cs" ] && target="$dir/game/Target.cs"
  echo "==> $name"
  if (cd "$dir" && LIDER_ROOT="$dir" LIDER_GAME="$game" LIDER_HOME="$root" LIDER_ACTIVE=1 LIDER_JAMO_DICT="$dict" \
    LIDER_TEST_FILE="$target" LIDER_JAMO_USER="$dir/jamo-user.dat" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
    timeout 180 "$player" "$entry"); then
    echo "ok $name"
  else
    echo "FAILED $name"
    status=1
  fi
  rm -rf "$dir"
done
exit "$status"
