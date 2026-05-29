#!/usr/bin/env bash
# NGS golden runner. title / play を各 frame で両 backend capture して比較。
#   samples/ngs/scripts/golden.sh            # check
#   samples/ngs/scripts/golden.sh --update   # regenerate
# play_* ケースは LUB_NGS_BOOT=play で Play 直入り。kill_ は intro を飛ばした Play + 手連射 mock
# (LUB_NGS_MOCK=kill) で敵#1 を即撃破し explosion を出す (弾ライン上端の小火花)。
set -euo pipefail
cd "$(dirname "$0")/../../.."   # -> lub repo root

BINARY=./build/lub
ENTRY=samples/ngs/ngs.hxml
GOLDEN_DIR=tests/golden/ngs
BACKENDS=(sokol sdlgpu)
# name:frame の組。play_=Play直入り, kill_=Play+撃破mock, boss_=boss直入り, gameover_=GameOver直入り。
CASES=(title_f0:0 title_f30:30 play_f0:0 play_f70:70 play_f120:120 play_f240:240 kill_f64:64 boss_f40:40 boss_f120:120 gameover_f30:30)

update=0
[[ "${1:-}" == "--update" ]] && update=1

mkdir -p "$GOLDEN_DIR"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
pass=0; fail=0; missing=0; updated=0

for c in "${CASES[@]}"; do
  name="${c%%:*}"; frame="${c##*:}"
  boot=""; mock=""
  [[ "$name" == play_* ]] && boot="play"
  [[ "$name" == kill_* ]] && { boot="active"; mock="kill"; }
  [[ "$name" == boss_* ]] && boot="boss"
  [[ "$name" == gameover_* ]] && boot="gameover"
  for bk in "${BACKENDS[@]}"; do
    out="$tmp/ngs_${name}_${bk}.png"
    golden="$GOLDEN_DIR/ngs_${name}_${bk}.png"
    LUB_BACKEND="$bk" LUB_NGS_BOOT="$boot" LUB_NGS_MOCK="$mock" scripts/run-headless.sh "$BINARY" "$ENTRY" \
      --capture "$out" --capture-frame "$frame" >"$tmp/${name}_${bk}.log" 2>&1
    if [[ ! -f "$out" ]]; then
      echo "FAIL ${name} ${bk}: no capture (see $tmp/${name}_${bk}.log)"; fail=$((fail+1)); continue
    fi
    if [[ $update -eq 1 ]]; then cp "$out" "$golden"; echo "UPDATED $golden"; updated=$((updated+1)); continue; fi
    if [[ ! -f "$golden" ]]; then echo "MISSING $golden"; missing=$((missing+1)); continue; fi
    if cmp -s "$out" "$golden"; then echo "PASS ${name} ${bk}"; pass=$((pass+1));
    else echo "FAIL ${name} ${bk}: $out != $golden"; fail=$((fail+1)); fi
  done
done
echo "---"
[[ $update -eq 1 ]] && { echo "updated: $updated"; exit 0; }
echo "pass: $pass  fail: $fail  missing: $missing"
[[ $fail -eq 0 && $missing -eq 0 ]]
