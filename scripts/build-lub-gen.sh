#!/usr/bin/env bash
# Converts tools/lub-gen (TinyC#) to C with the C tcs (tcs c) and builds <build-dir>/tcs/lub-gen.
# lub-gen reads the stubs and the game with the tcs compiler (src/tcs/Compiler), so that source is converted to C together with it.
# The program is src/tcs/Native/main.c, the same as for tcs (with the entries replaced by the Tool of lub-gen).
# The C conversion uses <build-dir>/tcs/tcs (and lib/) placed by scripts/build-tcs-native.sh. Then dotnet is not needed, only a C compiler.
# If <build-dir>/tcs/tcs is missing, it converts to C with the .NET tcs (the same implementation, producing the same C) and places lib/ from tcs (PR CI takes this path).
# The test bindings.lub-gen (tests/lub-test) checks that its output matches that of the same lub-gen run on .NET.
#
# Usage: scripts/build-lub-gen.sh [build-dir]   (default build-release-linux)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
build="${1:-build-release-linux}"
out="$build/tcs"
exe=""
libm=(-lm)
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    exe=".exe"
    libm=()
    ;;
esac
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

host_src=src/tcs
if [[ -x "$out/tcs$exe" ]]; then
  tcs_c=("$out/tcs$exe" c)
else
  tcs_c=(scripts/tcs.sh c)
  # lub-gen tcs, like tcs, reads lib/ from the directory of the executable
  src/tcs/ship.sh "$out"
fi
# The part of the tcs API that needs only the compiler (lub-gen does not generate C or .tcsb)
api=("$host_src"/Api/TcsResult.cs "$host_src"/Api/TcsInputs.cs "$host_src"/Api/TcsCompile.cs)
sources=("$host_src"/Compiler/*.cs "${api[@]}" tools/lub-gen/*.cs)
"${tcs_c[@]}" --lib --ref "$host_src/Api/TcsFiles.cs" --ref "$host_src/Cli/CliProcess.cs" "${sources[@]}" -o "$work/lub-gen.c"

cc="${CC:-cc}"
mapfile -t flags < <(src/tcs/cflags.sh)
"$cc" "${flags[@]}" -I "$work" -DTCS_MAIN_SOURCE='"lub-gen.c"' \
  -DTCS_MAIN_ARG=tcs_entry_LubGen_Tool_Arg -DTCS_MAIN_STEP=tcs_entry_LubGen_Tool_Step \
  "$host_src/Native/main.c" -o "$out/lub-gen$exe" ${libm[@]+"${libm[@]}"}
echo "lub-gen: $out/lub-gen$exe"
