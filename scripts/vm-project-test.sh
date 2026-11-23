#!/usr/bin/env bash
# Checks the path where the tcs VM player runs from a .csproj (tcs vm --watch, hot reload, OnBuildFailed, the inspection entry points of apps opened with App.Use, native extensions).
# Tests rewrite files, so tests/vm/<name>/ is copied to a temporary directory and run there. Tests are independent of each other, so as many run in parallel as there are cores.
# If tests/vm/<name>/env exists, its lines (NAME=VALUE) are added to the environment for the run.
# env: LUB_VM (the lub executable. Default build-release-linux/lub), LUB_TCS (the command to start tcs. If missing, lub looks for it)
#   LUB_VM_JOBS (number of tests run at the same time. Default is the number of cores)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
lub_vm="${LUB_VM:-$root/build-release-linux/lub}"
# shellcheck source=scripts/cs-tests.sh
source "$root/scripts/cs-tests.sh"

# Run one test and write its output to $2. Return non-zero if it fails
vm_project_test() {
  local name=$1 log=$2 test_dir="$root/tests/vm/$1/" dir ref stem src want missing=""
  local test_env=()
  dir="$(mktemp -d)"
  cp -r "$test_dir." "$dir/"
  # Build the C of a native extension (the lowercase .c next to <Name>.ref.cs) into a shared library of the same name
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
    # A test that checks for a crash: it must exit non-zero, and each line of expect-crash.txt must appear in the output
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
    # If expect.txt exists, each of its lines must appear in the output
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
