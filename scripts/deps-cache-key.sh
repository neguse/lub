#!/usr/bin/env bash
# CI の cache (actions/cache) に置く <build>/deps/ (cmake/LubDeps.cmake が第三者の library を install する先) の key を出す。
#   scripts/deps-cache-key.sh <toolchain の名前>
# key は、toolchain の名前、runner の OS と arch と image の版 (compiler の版はこれで変わる)、deps の CMake の記述、submodule の commit から作る。
# 名前は同じ runner の上の別の toolchain を分ける (例: native と wasm)。toolchain の版を workflow で決めるもの (emsdk) は、版も名前に入れる。
# cache が当たっても、cmake/LubDeps.cmake の key が合わなければ cmake が deps を作り直す (結果は変わらず、時間だけかかる)。
set -euo pipefail
cd "$(dirname "$0")/.."
name="$1"
sum="$(
  {
    git submodule status third_party/SDL third_party/box2d third_party/box3d third_party/openxr
    git hash-object third_party/CMakeLists.txt cmake/LubDeps.cmake
  } | git hash-object --stdin
)"
echo "deps-$name-${RUNNER_OS:-local}-${RUNNER_ARCH:-local}-${ImageVersion:-local}-$sum"
