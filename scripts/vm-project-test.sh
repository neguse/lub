#!/usr/bin/env bash
# tcs VM の player が .csproj から動かす経路 (tcs vm --watch、overlay、hot reload、OnBuildFailed、検査の口、native の拡張) の検査。
# test は file を書き換えるので、tests/vm/<name>/ を一時ディレクトリに写して動かす。
# env: LUB_VM (lub の実行ファイル。既定は build-release-linux/lub)、LUB_TCS (tcs の起動コマンド。無ければ lub が探す)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub_vm="${LUB_VM:-$root/build-release-linux/lub}"
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
  if [ -f "$dir/expect-crash.txt" ]; then
    # 落ちることを検査する test: 0 以外で終わり、expect-crash.txt の各行が出力に含まれること
    out="$dir/out.log"
    if (cd "$root" && GAME_CS="$dir/game/Game.cs" LUB_CS_LIB="$root/cs-lib" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
      timeout 180 "$lub_vm" "$dir/game/Game.csproj") >"$out" 2>&1; then
      echo "FAILED $name: did not crash"
      status=1
    else
      missing=""
      while IFS= read -r want; do
        grep -qF -- "$want" "$out" || missing="$missing [$want]"
      done <"$dir/expect-crash.txt"
      if [ -n "$missing" ]; then
        cat "$out"
        echo "FAILED $name: missing in the output:$missing"
        status=1
      else
        echo "ok $name"
      fi
    fi
  elif (cd "$root" && GAME_CS="$dir/game/Game.cs" LUB_CS_LIB="$root/cs-lib" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
    timeout 180 "$lub_vm" "$dir/game/Game.csproj") 2>&1 | tee "$dir/out.log"; then
    # expect.txt があれば、その各行が出力に含まれること
    missing=""
    if [ -f "$dir/expect.txt" ]; then
      while IFS= read -r want; do
        grep -qF -- "$want" "$dir/out.log" || missing="$missing [$want]"
      done <"$dir/expect.txt"
    fi
    if [ -n "$missing" ]; then
      echo "FAILED $name: missing in the output:$missing"
      status=1
    else
      echo "ok $name"
    fi
  else
    echo "FAILED $name"
    status=1
  fi
  rm -rf "$dir"
done
exit "$status"
