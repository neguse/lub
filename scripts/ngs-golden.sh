#!/usr/bin/env bash
# samples/ngs (C#) の scenario golden。LUB_NGS_BOOT / LUB_NGS_MOCK で scene と
# 入力 script を固定し、決まった frame を capture して tests/golden/ngs/ と
# byte 比較する (lavapipe、sdlgpu)。
#   scripts/ngs-golden.sh            # 比較
#   scripts/ngs-golden.sh --update   # 再生成
set -euo pipefail
cd "$(dirname "$0")/.."
BINARY="${BINARY:-./build-release-linux/lub}"
update=0
[ "${1:-}" = "--update" ] && update=1
scripts/run-cs-sample.sh ngs --build > /dev/null
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
status=0
# name frame BOOT MOCK
while read -r name frame boot mock; do
  png="$tmp/$name.png"
  env $([ "$boot" != - ] && echo LUB_NGS_BOOT=$boot) $([ "$mock" != - ] && echo LUB_NGS_MOCK=$mock) LUB_BACKEND=sdlgpu \
    scripts/run-headless.sh "$BINARY" samples/ngs/.lub/NgsMain.lua \
    --capture "$png" --capture-frame "$frame" --fixed-dt 0.0166666666666667 > "$tmp/$name.log" 2>&1
  golden="tests/golden/ngs/${name}_sdlgpu.png"
  if [ "$update" = 1 ]; then
    cp "$png" "$golden"
    echo "UPDATED $golden"
  elif cmp -s "$png" "$golden"; then
    echo "PASS ngs $name"
  else
    echo "FAIL ngs $name ($png vs $golden)"
    status=1
  fi
done <<'LIST'
title 30 - -
play 240 play fire
kill 64 active kill
boss 120 boss fire
gameover 30 gameover none
LIST
exit $status
