#!/usr/bin/env bash
# Builds the kana-kanji conversion dictionary (mozc.dict).
#   scripts/build-dict.sh <out-dir>     e.g.: scripts/build-dict.sh data (data/mozc.dict, which lider reads by default)
# Fetches mozc's dictionary_oss at the MOZC_REF commit below (third_party/jamo/scripts/fetch-mozc.sh) and turns it into a dictionary with the jamo CLI.
# The output is <out-dir>/mozc.dict and the notices shipped with the dictionary (the IPAdic and Okinawa dictionary terms in <out-dir>/mozc-README.txt, mozc's BSD-3-Clause in <out-dir>/mozc-LICENSE.txt).
# Converts 1 sentence with the built dictionary and checks that kanji come out.
# Requirements: cargo, curl.
set -euo pipefail
# The mozc commit to fetch. When bumping it, check that tests/ime passes with the built dictionary
mozc_ref=a4541df29db443b49f81cf54665873b68fde9efa
[ $# -eq 1 ] || {
  echo "usage: $0 <out-dir>" >&2
  exit 2
}
root="$(cd "$(dirname "$0")/.." && pwd)"
jamo="$(cd "$root/../third_party/jamo" && pwd)"
mkdir -p "$1"
out="$(cd "$1" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
MOZC_REF="$mozc_ref" sh "$jamo/scripts/fetch-mozc.sh" "$work/mozc"
# The jamo CLI build output goes on the lider side (keeps the jamo checkout clean)
cli=(cargo run -q --manifest-path "$jamo/Cargo.toml" -p jamo-cli --release --)
export CARGO_TARGET_DIR="$root/build/jamo-cli"
"${cli[@]}" dict build --dict "$work/mozc" -o "$out/mozc.dict"
cp "$work/mozc/README.txt" "$out/mozc-README.txt"
cp "$work/mozc/LICENSE" "$out/mozc-LICENSE.txt"
converted="$("${cli[@]}" convert --dict "$out/mozc.dict" きょうはいいてんきですね)"
echo "check: きょうはいいてんきですね -> $converted"
case "$converted" in
  *今日*) ;;
  *)
    echo "the dictionary did not convert to kanji" >&2
    exit 1
    ;;
esac
echo "wrote $out/mozc.dict"
