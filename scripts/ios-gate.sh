#!/usr/bin/env bash
# iOS tests: builds the macOS player (it fills the shader cache), builds the iOS
# app for the simulator, and runs the tests/cs tests meant for iOS there.
# The CI ios job (.github/workflows/ios.yml) and manual runs on a Mac use it.
# The macOS tests are run by the test runner (tests/lub-test).
#
# Needs CMake, Ninja, python3, dotnet (the tests are C#, and the test runner
# compiles them and compares the captures) and Xcode (not only the Command
# Line Tools). iOS requires SDL 3.4+ (UIScene lifecycle on iOS 27),
# uses AVAudioSession ambient at 48 kHz, and writes test exit codes to
# LUB_EXIT_FILE (verified alongside SpringBoard's `Process exited` log).
# Capture runs size the Metal drawable to the requested ConfigOpts dimensions
# (default 1280x720) rather than Retina window pixels so comparisons stay
# independent of display scaling.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

run() {
  echo
  echo "==> $*"
  "$@"
}

build=build-mac
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

# The tests in tests/cs are C#, so tcs vm first builds all of tests/cs into one bytecode (tests/cs/.lub/all.tcsb), and then --entry selects the test to run.
run dotnet run --project tests/lub-test -- cs-tests

# iOS: build the player app for the simulator and run it there. simctl does
# not report the app's exit status, so the app writes it to the file named by
# LUB_EXIT_FILE, and capture runs are compared to the Linux goldens like on
# macOS. The app has no shader compiler: its shaders come from the caches
# `lub export ios` writes here, one snapshot per test.
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

  # A parent that goes through xcrun, such as /usr/bin/python3, puts the macOS SDK in SDKROOT, so sysroot is set explicitly.
  # The simulator build uses Ninja (the Xcode generator starts xcodebuild for every feature-check try_compile, and configure alone takes minutes).
  run env -u SDKROOT cmake -S . -B build-ios -G Ninja \
    -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphonesimulator \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=16.0 \
    -DCMAKE_BUILD_TYPE=Release
  run cmake --build build-ios --target lub

  mkdir -p "$work/ios-cache"
  for cached in "${ios_capture_tests[@]}" "${ios_exit_tests[@]}"; do
    run "./$build/lub" export ios tests/cs/.lub/all.tcsb --entry "$cached" -o "$work/ios-$cached"
    if [[ -d "$work/ios-$cached/cache/shader" ]]; then
      cp "$work/ios-$cached/cache/shader/"* "$work/ios-cache/"
    fi
  done
  app=build-ios/lub.app
  mkdir -p "$app/tests/cs/.lub"
  cp tests/cs/.lub/all.tcsb "$app/tests/cs/.lub/"
  cp -R tests/cs/data "$app/tests/cs/"
  rm -rf "$app/cache"
  mkdir -p "$app/cache"
  cp -R "$work/ios-cache" "$app/cache/shader"

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
    if ios_launch "$work/ios.log" tests/cs/.lub/all.tcsb --entry "$ios_test" \
      --capture "$ios_data/tmp/$ios_test.png" "${capture[@]}" \
      && metrics="$(dotnet run --project tests/lub-test -- png-diff "$ios_data/tmp/$ios_test.png" \
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
      ios_launch "$work/ios.log" tests/cs/.lub/all.tcsb --entry "$ios_test" \
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

echo
echo "ios gate OK"
