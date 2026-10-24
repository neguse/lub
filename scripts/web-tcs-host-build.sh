#!/usr/bin/env bash
# tests/c/tcs_shutdown.cs を tcs2c で C にし、src/tcs_host.c の web の player (build/wasm-tcs/lub.js) を作る。
# 開いて確かめるのは web/scripts/tcs-shutdown-check.mjs。
# emcc (emsdk の環境) と dotnet が要る。
set -euo pipefail
cd "$(dirname "$0")/.."
out=build/wasm-tcs
mkdir -p "$out/gen"
dotnet build third_party/tcs/tcs2c/tcs2c.csproj -c Release -nologo -v q
dotnet build tools/lub-gen/lub-gen.csproj -c Release -nologo -v q
dotnet third_party/tcs/tcs2c/bin/Release/net10.0/tcs2c.dll --lib \
  --ref cs-lib/lub_stub.cs tests/c/tcs_shutdown.cs -o "$out/gen/game.c"
dotnet tools/lub-gen/bin/Release/net10.0/lub-gen.dll tcs --stub cs-lib/lub_stub.cs \
  --source tests/c/tcs_shutdown.cs -o "$out/gen/binding.c"
emcmake cmake -S . -B "$out" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DLUB_TCS_GAME="$PWD/$out/gen/game.c" -DLUB_TCS_BINDING="$PWD/$out/gen/binding.c"
cmake --build "$out" --target lub
