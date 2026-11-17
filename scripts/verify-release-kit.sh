#!/usr/bin/env bash
# release の一式 (scripts/package-release.sh の出力) だけを展開した所で、雛形のゲームを書いて動かす経路を確かめる。
# リポジトリの tcs や cs-lib は使わない。
# - lub export web で page にでき、shader の WGSL が入る
# - dotnet を PATH から外しても、一式の tcs で check でき、一式の lub-gen が接続コードを作れる
# - lider が雛形のゲームを開いて変換し、動かせる (絵を撮って終わる)。描画できる環境 (GPU か CPU の rasterizer、画面) は呼ぶ側が用意する
#
# Usage: scripts/verify-release-kit.sh <lub-<target>.tar.gz か lub-<target>.zip>
set -euo pipefail

archive="$(cd "$(dirname "${1:?archive}")" && pwd)/$(basename "$1")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
case "$archive" in
  *.zip)
    windows=1
    (cd "$work" && "$(cygpath -u "$SYSTEMROOT")/System32/tar.exe" -xf "$(cygpath -w "$archive")")
    ;;
  *)
    windows=0
    tar xf "$archive" -C "$work"
    ;;
esac
kit="$work/lub"
test -s "$kit/manual/tcs-support-matrix.md"
test -s "$kit/COMMIT"
test -s "$kit/lider/Lider.tcsb"
test -s "$kit/lider/data/mozc.dict"

cp -r "$kit/templates/game" "$work/mygame"
cd "$work/mygame"
../lub/bin/lub export web Game.csproj -o out
test -s out/index.html
grep -qa '@fragment' out/cache/shader/*.lubshader

# dotnet を PATH から外しても、一式の tcs で同じ雛形を check できること
PATH=/usr/bin:/bin ../lub/bin/lub check Game.csproj
# 一式の lub-gen も dotnet なしで stub を読み、雛形の接続コードを作れること (入力は tcs c と同じく、ゲームの後に cs-lib の実装)
gen_args=()
for f in Game.cs ../lub/cs-lib/lub/*.cs ../lub/cs-lib/lubx/*.cs; do gen_args+=(--source "$f"); done
PATH=/usr/bin:/bin ../lub/bin/tcs/lub-gen tcs --stub ../lub/cs-lib/lub_stub.cs "${gen_args[@]}" -o binding.c
test -s binding.c

# lider が雛形を開き、player に組み込んだ tcs で変換して動かす。変換した module は .lub/Game.tcsb に書く (上の lub が書いた物は消しておく)
rm -rf .lub
if [ "$windows" = 1 ]; then
  ../lub/bin/lider.cmd Game.csproj --capture lider.png --capture-frame 120
else
  ../lub/bin/lider Game.csproj --capture lider.png --capture-frame 120
fi
test -s lider.png
test -s .lub/Game.tcsb
echo "verified: $(basename "$archive")"
