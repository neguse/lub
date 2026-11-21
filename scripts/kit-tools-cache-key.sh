#!/usr/bin/env bash
# CI の cache (actions/cache) に置く C の tcs と lub-gen (<build>/tcs/、scripts/build-tcs-native.sh と scripts/build-lub-gen.sh の出力) の key を、GITHUB_OUTPUT の形で出す。
#   scripts/kit-tools-cache-key.sh <target> >> "$GITHUB_OUTPUT"
# どちらも、作るのに使う入力と、.NET の版との比較に使う入力の内容だけで決まる。入力が同じなら、比較を通った物をそのまま使う。
#   tcs:     tcs/ と、作る script、C compiler の版 (CC。build と同じものを渡す)
#   lub-gen: tcs の入力、tools/lub-gen/、比較で読む cs-lib/ と samples/13_sprites/、作る script
set -euo pipefail
cd "$(dirname "$0")/.."
target="$1"
tcs="$(
  {
    git rev-parse HEAD:tcs
    git hash-object scripts/build-tcs-native.sh
    "${CC:-cc}" --version
  } | git hash-object --stdin
)"
lub_gen="$(
  {
    echo "$tcs"
    git rev-parse HEAD:tools/lub-gen HEAD:cs-lib HEAD:samples/13_sprites
    git hash-object scripts/build-lub-gen.sh
  } | git hash-object --stdin
)"
echo "tcs=kit-tcs-$target-$tcs"
echo "lub-gen=kit-lub-gen-$target-$lub_gen"
