#!/usr/bin/env bash
# tcs VM の player が .csproj から動かす経路 (tcs vm --watch、hot reload、OnBuildFailed、App.Use で開いた app の検査の口、native の拡張) の検査。
# test は file を書き換えるので、tests/vm/<name>/ を一時ディレクトリに写して動かす。test どうしは独立なので、core の数だけ並べる。
# tests/vm/<name>/env があれば、その行 (NAME=VALUE) を環境に足して動かす。
# env: LUB_VM (lub の実行ファイル。既定は build-release-linux/lub)、LUB_TCS (tcs の起動コマンド。無ければ lub が探す)
#   LUB_VM_JOBS (同時に走らせる test の数。既定は core の数)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub_vm="${LUB_VM:-$root/build-release-linux/lub}"
# shellcheck source=scripts/cs-tests.sh
source "$root/scripts/cs-tests.sh"

# 1 本を走らせ、出力を $2 に書く。落ちたら 0 以外を返す
vm_project_test() {
  local name=$1 log=$2 test_dir="$root/tests/vm/$1/" dir ref stem src want missing=""
  local test_env=()
  dir="$(mktemp -d)"
  cp -r "$test_dir." "$dir/"
  # native の拡張の C (<Name>.ref.cs の隣の小文字の .c) を、同じ名前の共有 library にする
  for ref in "$dir"/game/*.ref.cs; do
    [ -e "$ref" ] || continue
    stem="${ref%.ref.cs}"
    src="$(dirname "$ref")/$(basename "$stem" | tr '[:upper:]' '[:lower:]').c"
    if [ -f "$src" ]; then
      "${CC:-cc}" -shared -fPIC -O2 -I"$root/include" -I"$root/tcs/tcsvm/vm" "$src" -o "$stem.so" >>"$log" 2>&1 || return 1
    fi
  done
  [ -f "$dir/env" ] && mapfile -t test_env <"$dir/env"
  if [ -f "$dir/expect-crash.txt" ]; then
    # 落ちることを検査する test: 0 以外で終わり、expect-crash.txt の各行が出力に含まれること
    if (cd "$root" && env "${test_env[@]}" GAME_CS="$dir/game/Game.cs" LUB_CS_LIB="$root/cs-lib" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
      timeout 180 "$lub_vm" "$dir/game/Game.csproj") >>"$log" 2>&1; then
      echo "FAILED $name: did not crash" >>"$log"
      return 1
    fi
    while IFS= read -r want; do
      grep -qF -- "$want" "$log" || missing="$missing [$want]"
    done <"$dir/expect-crash.txt"
  else
    if ! (cd "$root" && env "${test_env[@]}" GAME_CS="$dir/game/Game.cs" LUB_CS_LIB="$root/cs-lib" SDL_VIDEO_DRIVER="${SDL_VIDEO_DRIVER:-offscreen}" \
      timeout 180 "$lub_vm" "$dir/game/Game.csproj") >>"$log" 2>&1; then
      return 1
    fi
    # expect.txt があれば、その各行が出力に含まれること
    if [ -f "$dir/expect.txt" ]; then
      while IFS= read -r want; do
        grep -qF -- "$want" "$log" || missing="$missing [$want]"
      done <"$dir/expect.txt"
    fi
  fi
  rm -rf "$dir"
  if [ -n "$missing" ]; then
    echo "FAILED $name: missing in the output:$missing" >>"$log"
    return 1
  fi
}

names=()
for test_dir in "$root"/tests/vm/*/; do
  names+=("$(basename "$test_dir")")
done
echo "==> vm projects (${#names[@]})"
cs_run_tests "${LUB_VM_JOBS:-$(nproc)}" vm_project_test "${names[@]}"
