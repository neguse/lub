#!/usr/bin/env bash
# TestBindingsDigest を毎回 new / 使い回し / Clear し直しで走らせ、frame ごとの digest (C API 呼び出しの構造) が一致することを見る。
# tests/cs/.lub/TestBindingsDigest.lua を先に変換しておく (scripts/cs-tests.sh)。
# 使い方: scripts/test-bindings-digest.sh [lub binary]
set -euo pipefail
cd "$(dirname "$0")/.."
binary="${1:-./build-release-linux/lub}"
dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT

for mode in new reuse clear; do
  LUB_TEST_BINDINGS_MODE="$mode" scripts/run-headless.sh "$binary" tests/cs/.lub/TestBindingsDigest.lua \
    --fixed-dt 0.0166666666666667 --digest >"$dir/$mode.log" 2>&1 || { cat "$dir/$mode.log"; echo "FAIL: $mode exited non-zero" >&2; exit 1; }
  grep '^digest ' "$dir/$mode.log" >"$dir/$mode.digest"
  [[ -s "$dir/$mode.digest" ]] || { cat "$dir/$mode.log"; echo "FAIL: $mode printed no digest" >&2; exit 1; }
  echo "$mode: $(wc -l <"$dir/$mode.digest") frames, last $(tail -1 "$dir/$mode.digest")"
done
cmp "$dir/new.digest" "$dir/reuse.digest"
cmp "$dir/new.digest" "$dir/clear.digest"
echo "PASS"
