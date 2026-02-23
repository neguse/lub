#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Build Lua if not already built
if [ ! -f "$SCRIPT_DIR/deps/lua/lua" ]; then
  echo "Building Lua 5.5..."
  cmake -B "$SCRIPT_DIR/build" -DCMAKE_BUILD_TYPE=Release -S "$SCRIPT_DIR" >/dev/null 2>&1
  cmake --build "$SCRIPT_DIR/build" -j"$(nproc)" >/dev/null 2>&1
  echo "Lua built."
fi

# Run dotnet tests
echo "Running dotnet tests..."
dotnet test "$SCRIPT_DIR" --verbosity quiet
echo "All tests passed."
