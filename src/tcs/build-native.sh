#!/usr/bin/env bash
# Builds the C tcs into <dir>: tcs.c (tcs converted to C), the tcs executable, and the files shipped with it (runtime/, lib/).
#   src/tcs/build-native.sh <dir>
# The tcs that converts is scripts/tcs.sh (LUB_TCS if set, otherwise the .NET tcs).
# CC selects the C compiler (default cc. The generated C uses GNU C statement expressions, so MSVC's cl cannot be used. On Windows, clang).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$1"
dir="$(cd "$1" && pwd)"
exe=""
libm=(-lm)
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    exe=".exe"
    libm=()
    ;;
esac
"$here/to-c.sh" "$dir/tcs.c"
mapfile -t flags < <("$here/cflags.sh")
"${CC:-cc}" "${flags[@]}" -I "$dir" "$here/Native/host.c" -o "$dir/tcs$exe" ${libm[@]+"${libm[@]}"}
# Files of an earlier build that are no longer shipped do not stay
rm -rf "$dir/runtime" "$dir/lib"
"$here/ship.sh" "$dir"
