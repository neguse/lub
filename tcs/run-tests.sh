#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LUA_BIN="$SCRIPT_DIR/deps/lua/lua"
BUILD_DIR="$SCRIPT_DIR/build"

build_lua() {
  echo "Building Lua 5.5..."
  cmake -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE=Release -S "$SCRIPT_DIR" >/dev/null 2>&1

  local jobs="${TCS_JOBS:-}"
  if [ -z "$jobs" ]; then
    if command -v nproc >/dev/null 2>&1; then
      jobs="$(nproc)"
    elif command -v sysctl >/dev/null 2>&1; then
      jobs="$(sysctl -n hw.ncpu)"
    else
      jobs="2"
    fi
  fi

  cmake --build "$BUILD_DIR" --parallel "$jobs" >/dev/null 2>&1
  echo "Lua built."
}

needs_lua_build() {
  if [ ! -x "$LUA_BIN" ]; then
    return 0
  fi

  for input in "$SCRIPT_DIR/CMakeLists.txt" \
      "$SCRIPT_DIR/deps/lua/luaconf.h" \
      "$SCRIPT_DIR/deps/lua/lua.c"; do
    if [ "$input" -nt "$LUA_BIN" ]; then
      return 0
    fi
  done

  return 1
}

# Build Lua if missing or stale against the local build inputs
if needs_lua_build; then
  build_lua
fi

LUA_VERSION="$("$LUA_BIN" -v 2>&1)"
if [[ "$LUA_VERSION" != Lua\ 5.5* ]]; then
  echo "Lua binary version mismatch, rebuilding: $LUA_VERSION" >&2
  build_lua
  LUA_VERSION="$("$LUA_BIN" -v 2>&1)"
  if [[ "$LUA_VERSION" != Lua\ 5.5* ]]; then
    echo "Error: expected Lua 5.5, got: $LUA_VERSION" >&2
    exit 1
  fi
fi

# Run dotnet tests
echo "Running dotnet tests..."
dotnet test "$SCRIPT_DIR" --verbosity quiet

echo "Running tcs check on samples..."
sample_checks=(
  "samples/hello.cs"
  "samples/game.cs"
  "samples/inventory.cs"
  "samples/entity.cs"
  "samples/statemachine.cs"
  "samples/collision.cs"
)
for sample in "${sample_checks[@]}"; do
  dotnet run --project "$SCRIPT_DIR/Transpiler" -- check "$SCRIPT_DIR/$sample"
done
dotnet run --project "$SCRIPT_DIR/Transpiler" -- \
  check "$SCRIPT_DIR/samples/host_api_game.cs" \
  --ref "$SCRIPT_DIR/samples/host_api_stub.cs"

echo "Running analyzer demo build..."
set +e
analyzer_output="$(dotnet build "$SCRIPT_DIR/samples/analyzer-demo/analyzer-demo.csproj" --no-incremental 2>&1)"
analyzer_exit=$?
set -e
printf '%s\n' "$analyzer_output"
if [ "$analyzer_exit" -ne 0 ]; then
  exit "$analyzer_exit"
fi

count_diagnostic() {
  local id="$1"
  printf '%s\n' "$analyzer_output" \
    | awk -v id="$id" 'index($0, "warning " id ":") { seen[$0] = 1 } END { for (line in seen) count++; print count + 0 }'
}

tcs1001_count="$(count_diagnostic TCS1001)"
tcs1002_count="$(count_diagnostic TCS1002)"
if [ "$tcs1001_count" -ne 4 ] || [ "$tcs1002_count" -ne 1 ]; then
  echo "Error: analyzer demo expected TCS1001 x4 / TCS1002 x1, got TCS1001 x$tcs1001_count / TCS1002 x$tcs1002_count" >&2
  exit 1
fi

echo "All tests passed."
