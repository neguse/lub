#!/usr/bin/env bash
# Builds the lub player app for the iOS simulator into build-ios/ (build-ios/lub.app).
# The tests that run it on the simulator are the label ios of the test runner (tests/lub-test).
# Needs Xcode (not only the Command Line Tools), CMake and Ninja. iOS requires SDL 3.4+ (UIScene lifecycle on iOS 27).
set -euo pipefail
cd "$(dirname "$0")/.."
# A parent that goes through xcrun, such as /usr/bin/python3, puts the macOS SDK in SDKROOT, so the sysroot is given explicitly.
# The build uses Ninja: the Xcode generator starts xcodebuild for every feature-check try_compile, and configure alone takes minutes.
env -u SDKROOT cmake -S . -B build-ios -G Ninja \
  -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphonesimulator \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=16.0 \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build-ios --target lub
