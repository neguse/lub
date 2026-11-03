#!/usr/bin/env bash
# tcs VM の player が .csproj から動かす経路 (tcs vm --watch、overlay、hot reload、OnBuildFailed、検査の口、native の拡張) の検査。
# test は file を書き換えるので、tests/vm/<name>/ を一時ディレクトリに写して動かす。
# env: LUB_VM (lub_vm の実行ファイル。既定は build-release-linux/lub_vm)、LUB_TCS (tcs の起動コマンド。無ければ lub_vm が探す)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub_vm="${LUB_VM:-$root/build-release-linux/lub_vm}"
status=0
for test_dir in "$root"/tests/vm/*/; do
  name="$(basename "$test_dir")"
  dir="$(mktemp -d)"
  cp -r "$test_dir." "$dir/"
  # native の拡張の C (<Name>.ref.cs の隣の小文字の .c) を、同じ名前の共有 library にする
  for ref in "$dir"/game/*.ref.cs; do
    [ -e "$ref" ] || continue
    stem="${ref%.ref.cs}"
    src="$(dirname "$ref")/$(basename "$stem" | tr '[:upper:]' '[:lower:]').c"
    [ -f "$src" ] && "${CC:-cc}" -shared -fPIC -O2 -I"$root/include" -I"$root/third_party/tcs/tcsvm/vm" "$src" -o "$stem.so"
  done
  echo "==> vm project: $name"
  if (cd "$root" && GAME_CS="$dir/game/Game.cs" LUB_CS_LIB="$root/cs-lib" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
    timeout 180 "$lub_vm" "$dir/game/Game.csproj"); then
    echo "ok $name"
  else
    echo "FAILED $name"
    status=1
  fi
  rm -rf "$dir"
done
exit "$status"
