#!/usr/bin/env bash
# CI の cache (actions/cache) に置く <build>/deps/ (cmake/LubDeps.cmake が第三者の library を install する先) の key と restore-keys を、GITHUB_OUTPUT の形で出す。
#   scripts/deps-cache-key.sh <toolchain の名前> >> "$GITHUB_OUTPUT"
# key は、toolchain の名前、runner の OS と arch、deps の入力 (deps の CMake の記述と submodule の commit) の hash、runner の image の版 (compiler の版はこれで変わる) を並べる。
# 名前は同じ runner の上の別の toolchain を分ける (例: native と wasm)。toolchain の版を workflow で決めるもの (emsdk) は、版も名前に入れる。
# 同じ時期でも runner によって image の版が違うので、restore-keys は image の版の手前までにし、入力が同じで別の image の entry も使う。
# 使った物の compiler が違えば cmake/LubDeps.cmake の key が合わず、cmake が deps を作り直し、job がその image の key で保存する。
set -euo pipefail
cd "$(dirname "$0")/.."
name="$1"
sum="$(
  {
    git ls-files --stage third_party/SDL third_party/box2d third_party/box3d third_party/openxr
    git hash-object third_party/CMakeLists.txt cmake/LubDeps.cmake
  } | git hash-object --stdin
)"
restore="deps-$name-${RUNNER_OS:-local}-${RUNNER_ARCH:-local}-$sum-"
echo "key=$restore${ImageVersion:-local}"
echo "restore=$restore"
