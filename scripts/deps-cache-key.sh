#!/usr/bin/env bash
# Prints, in GITHUB_OUTPUT form, the key and restore-keys for <build>/deps/ (where cmake/LubDeps.cmake installs third-party libraries) stored in the CI cache (actions/cache).
#   scripts/deps-cache-key.sh <toolchain name> >> "$GITHUB_OUTPUT"
# The key joins the toolchain name, the OS and arch of the runner, the hash of the deps inputs (the CMake description of deps and the submodule commits), and the version of the runner image (the compiler version changes with it).
# The name separates different toolchains on the same runner (e.g. native and wasm). For toolchains whose version is set in the workflow (emsdk), the version also goes into the name.
# Even at the same time, the image version differs between runners, so restore-keys stop just before the image version, and entries with the same inputs from another image are also used.
# If the compiler of what was restored differs, the key of cmake/LubDeps.cmake does not match, cmake rebuilds deps, and the job saves them under the key of that image.
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
