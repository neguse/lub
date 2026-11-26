#!/usr/bin/env bash
# Builds the .NET tcs (src/tcs) into build-tcs/ and prints the command that starts it.
#   export LUB_TCS="$(scripts/build-tcs-dotnet.sh)"
# LUB_TCS is the one name for "the tcs to use": lub reads it (src/tcs_build.c), and so does scripts/tcs.sh.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
# A native program cannot read an MSYS path
if command -v cygpath >/dev/null 2>&1; then
  root="$(cygpath -m "$root")"
fi
# The output dir is given, so the place of tcs.dll does not depend on the target framework or on Platform in the environment
if ! log="$(dotnet build "$root/src/tcs" -c Release -nologo -v q -o "$root/build-tcs" 2>&1)"; then
  printf '%s\n' "$log" >&2
  exit 1
fi
echo "dotnet $root/build-tcs/tcs.dll"
