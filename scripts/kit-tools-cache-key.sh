#!/usr/bin/env bash
# Prints, in GITHUB_OUTPUT form, the key for the C tcs and lub-gen (<build>/tcs/, the output of scripts/build-tcs-native.sh and scripts/build-lub-gen.sh) stored in the CI cache (actions/cache).
#   scripts/kit-tools-cache-key.sh <target> >> "$GITHUB_OUTPUT"
# Both are determined only by the contents of the inputs used to build them and of the inputs used for the comparison with the .NET version. If the inputs are the same, the ones that passed the comparison are used as is.
#   tcs:     the tcs sources (src/tcs, src/tcs-system, src/tcs-runtime), the inputs of the comparison (tests/tcs, tests/tcs-vm) and the comparison itself (tests/lub-test), the build script, and the C compiler version (CC. Pass the same one as the build)
#   lub-gen: the inputs of tcs, tools/lub-gen/, cs-lib/ and samples/13_sprites/ read in the comparison, and the build script
set -euo pipefail
cd "$(dirname "$0")/.."
target="$1"
tcs="$(
  {
    git rev-parse HEAD:src/tcs HEAD:src/tcs-system HEAD:src/tcs-runtime HEAD:tests/tcs HEAD:tests/tcs-vm HEAD:tests/lub-test
    git hash-object scripts/build-tcs-native.sh
    "${CC:-cc}" --version
  } | git hash-object --stdin
)"
lub_gen="$(
  {
    echo "$tcs"
    git rev-parse HEAD:tools/lub-gen HEAD:cs-lib HEAD:samples/13_sprites
    git hash-object scripts/build-lub-gen.sh
  } | git hash-object --stdin
)"
echo "tcs=kit-tcs-$target-$tcs"
echo "lub-gen=kit-lub-gen-$target-$lub_gen"
