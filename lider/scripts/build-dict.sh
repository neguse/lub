#!/usr/bin/env bash
# かな漢字変換の辞書 (mozc.dict) を作る。
#   scripts/build-dict.sh <out-dir>     例: scripts/build-dict.sh data (lider が既定で読む data/mozc.dict)
# mozc の dictionary_oss を下の MOZC_REF の commit で取り (third_party/jamo/scripts/fetch-mozc.sh)、jamo の CLI で辞書にする。
# 出力は <out-dir>/mozc.dict と、辞書と一緒に配る表示 (<out-dir>/mozc-README.txt に IPAdic と沖縄辞書の条項、<out-dir>/mozc-LICENSE.txt に mozc の BSD-3-Clause)。
# 作った辞書で 1 文を変換し、漢字が出ることを確かめる。
# 要るもの: cargo、curl。
set -euo pipefail
# 取る mozc の commit。上げたら、作った辞書で tests/ime が通ることを確かめる
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
# jamo の CLI の build の出力は lider の側に置く (jamo の checkout を汚さない)
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
