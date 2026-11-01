#!/usr/bin/env bash
# tcs2c で C にした tcs (third_party/tcs/SelfHost/native.sh) を作り、<build-dir>/tcs に置く。
# lub は実行ファイルの隣の tcs/tcs を最初に使う (src/tcs_build.c)。tcs は一緒に配るファイル (runtime/、lib/) を同じ dir から読む。
# native.sh は作った tcs が .NET の tcs と同じ出力を出すことも確かめる。作るのに dotnet SDK と C compiler が要る。使うのには要らない。
#
# Usage: scripts/build-tcs-native.sh [build-dir]   (既定 build-release-linux)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
build="${1:-build-release-linux}"
native=third_party/tcs/SelfHost/out/native
out="$build/tcs"

bash third_party/tcs/SelfHost/native.sh
rm -rf "$out"
mkdir -p "$out"
for f in tcs tcs.exe; do
  [[ -f "$native/$f" ]] && cp "$native/$f" "$out/"
done
cp -R "$native/runtime" "$native/lib" "$out/"
echo "tcs: $out/tcs"
