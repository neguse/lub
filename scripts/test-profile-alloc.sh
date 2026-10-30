#!/usr/bin/env bash
# LUB_PROFILE の LUB_PROFILE_LUA の行が、フレームごとの確保量と GC の周回を出すことを見る。
# tests/cs/.lub/TestProfileAlloc.lua を先に変換しておく (scripts/cs-tests.sh)。
# 使い方: scripts/test-profile-alloc.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT

# $1 = フレームごとの object 数。
# 結果を avg_alloc_kb / max_alloc_kb / avg_free_kb / gc_cycles に入れる
run() {
  LUB_PROFILE=1 LUB_PROFILE_START_FRAME=10 LUB_PROFILE_ALLOC_TEST_N="$1" \
    scripts/run-headless.sh "$binary" tests/cs/.lub/TestProfileAlloc.lua >"$log" 2>&1
  local line
  line="$(grep 'LUB_PROFILE_LUA' "$log" | tail -1 || true)"
  if [[ -z "$line" ]]; then
    cat "$log"
    echo "FAIL: no LUB_PROFILE_LUA line" >&2
    exit 1
  fi
  echo "$1 objects per frame: $line"
  field() { sed -n "s/.* $1=\([0-9.]*\).*/\1/p" <<<"$line"; }
  avg_alloc_kb="$(field avg_alloc_kb)"
  max_alloc_kb="$(field max_alloc_kb)"
  avg_free_kb="$(field avg_free_kb)"
  gc_cycles="$(field gc_cycles)"
}

# awk の条件が成り立たなければ失敗する
expect() {
  awk "BEGIN { exit !($1) }" || {
    echo "FAIL: $2 ($1)" >&2
    exit 1
  }
}

run 0
expect "$avg_alloc_kb < 20" "a frame that makes no garbage should allocate little"

# object 1 個は table で 50 byte を超える
run 2000
expect "$avg_alloc_kb > 100" "2000 objects per frame should allocate over 100 KB per frame"
expect "$max_alloc_kb >= $avg_alloc_kb" "the largest frame cannot be below the average"
expect "$avg_free_kb > 0" "the GC should free the garbage"
expect "$gc_cycles >= 1" "the GC should finish a cycle"

echo "LUB_PROFILE_LUA: allocation per frame and GC cycles follow the garbage the game makes"
