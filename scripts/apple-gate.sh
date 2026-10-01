#!/usr/bin/env bash
# Apple regression gate: macOS Release build -> C smoke tests -> Lua runtime
# tests -> visual goldens (Metal on the real GPU, within a tolerance of the
# Linux goldens) -> shader cache round trip through a player without Slang
# -> iOS build. Single source of truth shared by the CI macos job
# (.github/workflows/ci.yml) and manual runs on a Mac.
#
# Needs bash 4+ (run-golden.sh uses mapfile / wait -n; macOS ships 3.2, so
# `brew install bash`), CMake, Ninja, python3, and for the C# golden samples
# dotnet with the third_party/tcs submodule. The iOS step needs Xcode.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

skip_ios=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-ios)
      skip_ios=1
      ;;
    -h|--help)
      cat <<'EOF'
Usage: scripts/apple-gate.sh [--skip-ios]

Runs the macOS / iOS regression gate. --skip-ios leaves out the iOS build
(it needs Xcode, not only the Command Line Tools).
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

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

run bash scripts/build-release.sh --build-dir "$build"
for smoke in lub_physics_box2d_smoke lub_physics_box3d_smoke \
  lub_surfacenets_smoke lub_sdf_smoke lub_shader_layout_smoke; do
  run bash scripts/build-release.sh --build-dir "$build" --target "$smoke" --no-configure
  run "./$build/$smoke"
done

run "./$build/lub" tests/lua/test_fixed_dt.lua --fixed-dt 0.0125

# shellcheck source=scripts/lua-tests.sh
source scripts/lua-tests.sh
echo
echo "==> Lua runtime tests (${#lua_runtime_tests[@]})"
for lua_test in "${lua_runtime_tests[@]}"; do
  if "./$build/lub" "$lua_test" >"$work/lua.log" 2>&1; then
    echo "PASS $lua_test"
  else
    echo "FAIL $lua_test"
    sed 's/^/    /' "$work/lua.log"
    exit 1
  fi
done

# Visual goldens. The samples are C#, so without dotnet only the raw Lua
# visual tests run.
if command -v dotnet >/dev/null 2>&1 \
  && [[ -f third_party/tcs/Transpiler/Transpiler.csproj ]]; then
  run dotnet build third_party/tcs/Transpiler -c Release -nologo
  LUB_TCS_DLL="$repo_root/third_party/tcs/Transpiler/bin/Release/net10.0/Transpiler.dll"
  export LUB_TCS_DLL
  run env BINARY="./$build/lub" scripts/run-golden.sh
else
  echo
  echo "==> C# golden samples SKIPPED (dotnet or third_party/tcs missing)"
  run env BINARY="./$build/lub" scripts/run-golden.sh --tests-only
fi

# Shader cache: a player with Slang fills it, a player without Slang (what an
# iOS app is) renders the same frame from it, and names the key when a shader
# is missing.
run bash scripts/build-release.sh --build-dir "$player" -DLUB_NO_SLANG=ON
capture=(--capture-frame 30 --fixed-dt 0.0166666666666667)
for cached in test_vertex_pull test_depth_sample; do
  run env LUB_SHADER_CACHE="$work/shader-cache" "./$build/lub" \
    "tests/lua/$cached.lua" --capture "$work/$cached-slang.png" "${capture[@]}"
  run env LUB_SHADER_CACHE="$work/shader-cache" "./$player/lub" \
    "tests/lua/$cached.lua" --capture "$work/$cached-cache.png" "${capture[@]}"
  run cmp "$work/$cached-slang.png" "$work/$cached-cache.png"
done
mkdir "$work/empty-cache"
echo
echo "==> a missing shader is reported by its cache key"
LUB_SHADER_CACHE="$work/empty-cache" "./$player/lub" tests/lua/test_vertex_pull.lua \
  --capture "$work/miss.png" --capture-frame 2 --fixed-dt 0.0166666666666667 \
  >"$work/miss.log" 2>&1 || true
if ! grep -q "this player has no shader compiler" "$work/miss.log"; then
  sed 's/^/    /' "$work/miss.log"
  echo "expected a shader cache miss error" >&2
  exit 1
fi

# iOS: the player has to build for the simulator. Running it is not part of
# the gate.
if [[ $skip_ios -eq 1 ]]; then
  echo
  echo "==> iOS build SKIPPED (--skip-ios)"
else
  # /usr/bin/python3 などの xcrun 経由の親は SDKROOT に macOS の SDK を入れる
  # ので、sysroot は明示する。
  run env -u SDKROOT cmake -S . -B build-ios -G Xcode \
    -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphoneos \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=16.0
  run cmake --build build-ios --target lub --config Release -- \
    -sdk iphonesimulator -arch arm64 CODE_SIGNING_ALLOWED=NO -quiet
fi

echo
echo "apple gate OK"
