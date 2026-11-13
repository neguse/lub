#!/usr/bin/env bash
# エディタの core (src/Editor) の test (tests/editor/<name>/) を、lider を動かさずに単独の entry として走らせる。
# test ごとに、test の dir の *.cs と tests/editor/EditorTest.cs と src/Editor と src/Core/{Utf8,Keys}.cs と src/Disk.cs を tcs VM に変換して player で動かす。
# player は scripts/player.sh で決める (かな漢字変換は lider の player に静的に link した jamo か、lub が読む src/Jamo.so)。
# test は file を書くので、一時ディレクトリを cwd にして起動する。
# env: PLAYER / LUB_TCS は scripts/player.sh
#   JAMO_DICT かな漢字変換の辞書 (既定: lider の data/mozc.dict。無ければ IME の test は変換を確かめずに通す)
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
