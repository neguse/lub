#!/usr/bin/env bash
# Converts tests/c/tcs_shutdown.cs to C with `tcs c` and builds the web player of src/tcs_host.c (build/wasm-tcs/lub.js).
# web/scripts/tcs-shutdown-check.mjs opens it and checks it.
# Requires emcc (the emsdk environment) and dotnet.
set -euo pipefail
cd "$(dirname "$0")/.."
out=build/wasm-tcs
mkdir -p "$out/gen"
LUB_TCS="${LUB_TCS:-$(scripts/build-tcs-dotnet.sh)}"
export LUB_TCS
dotnet build tools/lub-gen/lub-gen.csproj -c Release -nologo -v q
scripts/tcs.sh c --lib \
  --ref cs-lib/lub_stub.cs tests/c/tcs_shutdown.cs -o "$out/gen/game.c"
dotnet tools/lub-gen/bin/Release/net11.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_shutdown.cs -o "$out/gen/binding.c"
emcmake cmake -S . -B "$out" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DLUB_TCS_GAME="$PWD/$out/gen/game.c" -DLUB_TCS_BINDING="$PWD/$out/gen/binding.c"
cmake --build "$out" --target lub
