#!/usr/bin/env bash
# Converts tools/lub-gen (TinyC#) to C with the C tcs (tcs c) and builds <build-dir>/tcs/lub-gen.
# lub-gen reads the stubs and the game with the tcs compiler (src/tcs/Compiler), so that source is converted to C together with it.
# The host is src/tcs/Native/host.c, the same as for tcs (with the entry replaced by the Tool of lub-gen).
# The C conversion uses <build-dir>/tcs/tcs (and lib/) placed by scripts/build-tcs-native.sh. Then dotnet is not needed, only a C compiler.
# If <build-dir>/tcs/tcs is missing, it converts to C with the .NET tcs (the same implementation, producing the same C) and places lib/ from tcs (PR CI takes this path).
# With --verify, it also checks that the output (the generated files of gen-api and the tcs glue code) matches that of the same lub-gen run on .NET (requires dotnet).
#
# Usage: scripts/build-lub-gen.sh [--verify] [build-dir]   (default build-release-linux)
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
verify=0
if [[ "${1:-}" == "--verify" ]]; then
  verify=1
  shift
fi
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
  # lub-gen tcs, like tcs, reads lib/TinySystem from the directory of the executable
  mkdir -p "$out/lib/TinySystem"
  cp src/tcs-system/Linq.cs src/tcs-system/ValueTuple.cs "$out/lib/TinySystem/"
fi
sources=("$host_src"/Compiler/*.cs tools/lub-gen/*.cs)
"${tcs_c[@]}" --lib --ref "$host_src/Tool/TcsHost.cs" "${sources[@]}" -o "$work/lub-gen.c"

# Floating-point rules (appendix B of the tcs il-spec). Omitted for compilers that do not accept -fexcess-precision=standard (same as src/tcs/native.sh)
cc="${CC:-cc}"
flags=(-O2 -ffp-contract=off -fwrapv)
printf 'int main(void){return 0;}\n' > "$work/probe.c"
if "$cc" -fexcess-precision=standard -Werror -c "$work/probe.c" -o "$work/probe.o" >/dev/null 2>&1; then
  flags+=(-fexcess-precision=standard)
fi
"$cc" "${flags[@]}" -I "$work" -DTCS_HOST_SOURCE='"lub-gen.c"' \
  -DTCS_HOST_START=tcs_entry_LubGen_Tool_Start -DTCS_HOST_STEP=tcs_entry_LubGen_Tool_Step \
  "$host_src/Native/host.c" -o "$out/lub-gen$exe" ${libm[@]+"${libm[@]}"}
echo "lub-gen: $out/lub-gen$exe"

[[ $verify == 1 ]] || exit 0
native="$out/lub-gen$exe"
dotnet build tools/lub-gen -c Release -nologo -v q -o "$work/dotnet" > /dev/null
dotnet_gen=(dotnet "$work/dotnet/lub-gen.dll")
fail=0
same() {
  if cmp -s "$1" "$2"; then
    echo "  same: $3"
  else
    echo "  DIFFERENT: $3" >&2
    # Print the first difference (so that a trailing \r is also visible)
    diff "$1" "$2" | head -12 | cat -A | cut -c1-200 >&2 || true
    fail=1
  fi
}
for verb in header vm docs facade facade-lubx model; do
  "$native" "$verb" -o "$work/native.$verb"
  "${dotnet_gen[@]}" "$verb" -o "$work/dotnet.$verb"
  same "$work/native.$verb" "$work/dotnet.$verb" "lub-gen $verb"
done
lubx_args=()
for source in cs-lib/lub/*.cs cs-lib/lubx/*.cs samples/13_sprites/Sprites13.cs; do
  lubx_args+=(--source "$source")
done
# lub-gen tcs reads lib/TinySystem from the directory of the executable (same as tcs)
"$native" tcs --stub cs-lib/lub_stub.cs "${lubx_args[@]}" -o "$work/native.tcs"
"${dotnet_gen[@]}" tcs --stub cs-lib/lub_stub.cs "${lubx_args[@]}" -o "$work/dotnet.tcs"
same "$work/native.tcs" "$work/dotnet.tcs" "lub-gen tcs (cs-lib + 13_sprites)"
exit $fail
