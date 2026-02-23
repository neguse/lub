#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Build Lua if not already built
if [ ! -f "$SCRIPT_DIR/deps/lua/lua" ]; then
  echo "Building Lua 5.5..."
  make -C "$SCRIPT_DIR/deps/lua" -j"$(nproc)"
fi

# Run dotnet tests
echo "Running dotnet tests..."
dotnet test "$SCRIPT_DIR" --verbosity quiet
echo "All tests passed."
