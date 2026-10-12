#!/usr/bin/env bash
# Apple regression gate: macOS Release build -> C smoke tests -> C# runtime
# tests (tests/cs) -> visual goldens (Metal on the real GPU, within a tolerance of the
# Linux goldens) -> shader cache round trip through a player without Slang
# -> iOS build and a run on the simulator. Single source of truth shared by
# the CI macos / ios jobs
# (.github/workflows/ci.yml) and manual runs on a Mac.
#
# Needs bash 4+ (run-golden.sh uses mapfile / wait -n; macOS ships 3.2, so
# `brew install bash`), CMake, Ninja, python3, and dotnet with the
# third_party/tcs submodule (the tests and the golden samples are C#). The iOS
# step needs Xcode.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

part=all
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-ios)
      part=mac
      ;;
    --part)
      part="${2:-}"
      shift
      [[ $part == mac || $part == ios || $part == all ]] || {
        echo "--part must be mac, ios or all" >&2
        exit 2
      }
      ;;
    -h|--help)
      cat <<'EOF'
Usage: scripts/apple-gate.sh [--part mac|ios|all] [--skip-ios]

Runs the macOS / iOS regression gate (all parts by default).
  --part mac   the macOS build, smoke tests, C# runtime tests, goldens and the
               shader cache round trip (--skip-ios is the same)
  --part ios   the macOS build (it fills the shader cache), the iOS build and
               the simulator run. Needs Xcode, not only the Command Line Tools.
CI runs the two parts as separate jobs, side by side.
EOF
      exit 0
      ;;
    *)
      echo "unknown arg: $1" >&2
      exit 2
      ;;
  esac
  shift
done

run() {
  echo
  echo "==> $*"
  "$@"
}

build=build-mac
player=build-mac-player
export LUB_BACKEND=metal
# A failed SDL assertion would otherwise sit in a message box nobody clicks.
export SDL_ASSERT="${SDL_ASSERT:-abort}"
capture=(--capture-frame 30 --fixed-dt 0.0166666666666667)

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

ios_pick_device() {
  xcrun simctl list devices available -j | python3 -c '
import json, sys
runtimes = json.load(sys.stdin)["devices"]
def version(name):
    return [int(n) for n in name.rsplit("iOS-", 1)[-1].split("-")]
for name in sorted((n for n in runtimes if "SimRuntime.iOS-" in n), key=version, reverse=True):
    for device in runtimes[name]:
        if device["name"].startswith("iPhone"):
            print(device["udid"])
            sys.exit(0)
sys.exit("no iPhone simulator is available")
'
}

run bash scripts/build-release.sh --build-dir "$build"

# tests/cs の test は C# なので、tcs で tests/cs/.lub/<Entry>.lua に変換してから
# 動かす (iOS の app にはその Lua を入れる)。
run dotnet build third_party/tcs/Transpiler -c Release -nologo
LUB_TCS_DLL="$repo_root/third_party/tcs/Transpiler/bin/Release/net10.0/Transpiler.dll"
export LUB_TCS_DLL
# shellcheck source=scripts/cs-tests.sh
source scripts/cs-tests.sh
run cs_tests_transpile "${cs_all_tests[@]}"

if [[ $part != ios ]]; then
  for smoke in lub_physics_box2d_smoke lub_physics_box3d_smoke \
    lub_surfacenets_smoke lub_sdf_smoke lub_shader_layout_smoke; do
    run bash scripts/build-release.sh --build-dir "$build" --target "$smoke" --no-configure
    run "./$build/$smoke"
  done

  run "./$build/lub" tests/cs/.lub/TestFixedDt.lua --fixed-dt 0.0125

  echo
  echo "==> C# runtime tests (${#cs_runtime_tests[@]})"
  for cs_test in "${cs_runtime_tests[@]}"; do
    if "./$build/lub" "tests/cs/.lub/$cs_test.lua" >"$work/test.log" 2>&1; then
      echo "PASS $cs_test"
    else
      echo "FAIL $cs_test"
      sed 's/^/    /' "$work/test.log"
      exit 1
    fi
  done

  run env BINARY="./$build/lub" scripts/run-golden.sh

  # Shader cache: a player with Slang fills it, a player without Slang (what an
  # iOS app is) renders the same frame from it, and names the key when a shader
  # is missing.
  run bash scripts/build-release.sh --build-dir "$player" -DLUB_NO_SLANG=ON
  for cached in TestVertexPull TestDepthSample; do
    run env LUB_SHADER_CACHE="$work/shader-cache" "./$build/lub" \
      "tests/cs/.lub/$cached.lua" --capture "$work/$cached-slang.png" "${capture[@]}"
    run env LUB_SHADER_CACHE="$work/shader-cache" "./$player/lub" \
      "tests/cs/.lub/$cached.lua" --capture "$work/$cached-cache.png" "${capture[@]}"
    run cmp "$work/$cached-slang.png" "$work/$cached-cache.png"
  done
  mkdir "$work/empty-cache"
  echo
  echo "==> a missing shader is reported by its cache key"
  LUB_SHADER_CACHE="$work/empty-cache" "./$player/lub" tests/cs/.lub/TestVertexPull.lua \
    --capture "$work/miss.png" --capture-frame 2 --fixed-dt 0.0166666666666667 \
    >"$work/miss.log" 2>&1 || true
  if ! grep -q "this player has no shader compiler" "$work/miss.log"; then
    sed 's/^/    /' "$work/miss.log"
    echo "expected a shader cache miss error" >&2
    exit 1
  fi
fi

# iOS: build the player app for the simulator and run it there. simctl does
# not report the app's exit status, so the app writes it to the file named by
# LUB_EXIT_FILE, and capture runs are compared to the Linux goldens like on
# macOS. The app has no shader compiler: its shaders
# come from a cache the Mac player fills here.
ios_capture_tests=(TestIndexedDraw TestLoadOp TestDepthSample TestVertexPull)
ios_exit_tests=(TestVertexTexture TestDupBinding TestDispatchAfterReadback
  TestComputeUniformBlock TestAudio TestMathDeterminism)
ios_bundle=dev.neguse.lub
ios_device=""
ios_since=""

# The simulator's log entries since the last launch that match a predicate
# (only the lines that start with a timestamp: not log's own header nor the
# rest of a multi-line message).
ios_log() {
  xcrun simctl spawn "$ios_device" log show --style compact --start "$ios_since" \
    --predicate "$1" 2>&1 | grep -E '^[0-9]{4}-[0-9]{2}-[0-9]{2} ' || true
}

# How the app ended is only in SpringBoard's log ("Process exited: <...
# voluntary>", or the signal / watchdog that ended it). ios_launch keeps that
# line in $log.exit and fails unless the app exited by itself.
ios_launch() {
  local log="$1" waited=0 job app_pid
  shift
  ios_since="$(date '+%Y-%m-%d %H:%M:%S')"
  : >"$log.exit"
  touch "$log.start"
  xcrun simctl launch --console-pty "$ios_device" "$ios_bundle" "$@" >"$log" 2>&1 &
  job=$!
  while kill -0 "$job" 2>/dev/null; do
    if ((waited >= 120)); then
      xcrun simctl terminate "$ios_device" "$ios_bundle" >/dev/null 2>&1 || true
      wait "$job" 2>/dev/null || true
      echo "    timed out after ${waited}s"
      return 1
    fi
    sleep 1
    waited=$((waited + 1))
  done
  wait "$job" 2>/dev/null || true
  app_pid="$(grep -o "$ios_bundle: [0-9]*" "$log" | head -n 1 | cut -d ' ' -f 2)"
  if [[ -z $app_pid ]]; then
    echo "    the app did not launch"
    return 1
  fi
  for ((waited = 0; waited < 5; waited++)); do
    ios_log "process == \"SpringBoard\" AND eventMessage CONTAINS \"Process exited\" \
AND eventMessage CONTAINS \"<$ios_bundle>:$app_pid\"" >"$log.exit"
    [[ -s $log.exit ]] && break
    sleep 1
  done
  if [[ ! -s $log.exit ]]; then
    echo "    SpringBoard did not report how pid $app_pid ended"
  elif ! grep -q "voluntary" "$log.exit"; then
    return 1
  fi
}

# What is known about a failed launch: the app's console output, how it ended,
# what the app and the system logged about it, and any crash report.
ios_explain() {
  local log="$1" mark="${2:-}" report word words=""
  sed 's/^/    /' "$log"
  if [[ -n $mark ]]; then
    if [[ -f $mark ]]; then
      sed 's/^/    marker: /' "$mark"
    else
      echo "    marker: none (the test did not finish)"
    fi
  fi
  sed 's/^/    exit: /' "$log.exit"
  echo "    app log:"
  ios_log 'process == "lub"' | tail -n 40 | sed 's/^/      /'
  echo "    system log about the app:"
  for word in exit terminat watchdog kill crash jetsam; do
    words+="${words:+ OR }eventMessage CONTAINS[c] \"$word\""
  done
  ios_log "(process == \"SpringBoard\" OR process == \"runningboardd\") \
AND eventMessage CONTAINS \"$ios_bundle\" AND ($words)" | tail -n 40 | sed 's/^/      /'
  while IFS= read -r report; do
    echo "    crash report $report:"
    head -n 60 "$report" | sed 's/^/      /'
  done < <(find ~/Library/Logs/DiagnosticReports -maxdepth 1 -name 'lub*' -newer "$log.start" \
    2>/dev/null)
}

if [[ $part == mac ]]; then
  echo
  echo "==> iOS build and simulator run SKIPPED (--part mac)"
else
  # /usr/bin/python3 などの xcrun 経由の親は SDKROOT に macOS の SDK を入れる
  # ので、sysroot は明示する。simulator 向けは Ninja で build する (Xcode
  # generator は機能検査の try_compile ごとに xcodebuild を起こし、configure
  # だけで数分かかる)。
  run env -u SDKROOT cmake -S . -B build-ios -G Ninja \
    -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphonesimulator \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=16.0 \
    -DCMAKE_BUILD_TYPE=Release
  run cmake --build build-ios --target lub

  for cached in "${ios_capture_tests[@]}"; do
    run env LUB_SHADER_CACHE="$work/shader-cache" "./$build/lub" "tests/cs/.lub/$cached.lua" \
      --capture "$work/bake.png" "${capture[@]}"
  done
  for cached in "${ios_exit_tests[@]}"; do
    run env LUB_SHADER_CACHE="$work/shader-cache" "./$build/lub" "tests/cs/.lub/$cached.lua"
  done
  app=build-ios/lub.app
  mkdir -p "$app/samples" "$app/third_party/lume" "$app/tests/cs/.lub"
  cp samples/boot.lua "$app/samples/"
  cp third_party/lume/lume.lua "$app/third_party/lume/"
  cp tests/cs/.lub/*.lua "$app/tests/cs/.lub/"
  cp -R tests/cs/data "$app/tests/cs/"
  rm -rf "$app/shader-cache"
  cp -R "$work/shader-cache" "$app/shader-cache"

  ios_device="$(ios_pick_device)"
  # Booting is not overlapped with the builds or tests: on a fresh runner it
  # keeps the CPU busy for minutes and slows whatever runs beside it as much.
  run xcrun simctl boot "$ios_device" || true # already booted
  run xcrun simctl bootstatus "$ios_device" -b
  run xcrun simctl install "$ios_device" "$app"
  ios_data="$(xcrun simctl get_app_container "$ios_device" "$ios_bundle" data)"
  export SIMCTL_CHILD_LUB_BACKEND=metal
  ios_failed=0
  for ios_test in "${ios_capture_tests[@]}"; do
    rm -f "$ios_data/tmp/$ios_test.png"
    if ios_launch "$work/ios.log" "tests/cs/.lub/$ios_test.lua" \
      --capture "$ios_data/tmp/$ios_test.png" "${capture[@]}" \
      && metrics="$(python3 scripts/png-diff.py "$ios_data/tmp/$ios_test.png" \
        "tests/golden/${ios_test}_sdlgpu.png" 2>&1)"; then
      echo "PASS ios $ios_test $metrics"
    else
      echo "FAIL ios $ios_test ${metrics:-}"
      ios_explain "$work/ios.log"
      ios_failed=1
    fi
  done
  for ios_test in "${ios_exit_tests[@]}"; do
    mark="$ios_data/tmp/$ios_test.exit"
    rm -f "$mark"
    if SIMCTL_CHILD_LUB_EXIT_FILE="$mark" \
      ios_launch "$work/ios.log" "tests/cs/.lub/$ios_test.lua" \
      && grep -qx 0 "$mark" 2>/dev/null; then
      echo "PASS ios $ios_test"
    else
      echo "FAIL ios $ios_test"
      ios_explain "$work/ios.log" "$mark"
      ios_failed=1
    fi
  done
  # A CI runner is thrown away, so skip the cleanup there (shutting the
  # simulator down takes tens of seconds).
  if [[ -z "${CI:-}" ]]; then
    xcrun simctl uninstall "$ios_device" "$ios_bundle" || true
    xcrun simctl shutdown "$ios_device" || true
  fi
  if [[ $ios_failed -ne 0 ]]; then
    exit 1
  fi
fi

echo
echo "apple gate OK"
