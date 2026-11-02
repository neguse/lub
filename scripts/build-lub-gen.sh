#!/usr/bin/env bash
# tools/lub-gen (TinyC#) を C の tcs (tcs c) で C にし、<build-dir>/tcs/lub-gen を作る。
# lub-gen は tcs の compiler (third_party/tcs/SelfHost/Compiler) で stub とゲームを読むので、そのソースと一緒に C にする。
# host は tcs と同じ third_party/tcs/SelfHost/Native/host.c (entry を lub-gen の Tool に差し替える)。
# C にするのは scripts/build-tcs-native.sh が置いた <build-dir>/tcs/tcs (と lib/)。それなら dotnet は要らず、C compiler だけ要る。
# <build-dir>/tcs/tcs が無ければ、.NET の tcs2c (tcs c と同じ実装で、同じ C を出す) で C にし、lib/ を third_party/tcs から置く (PR の CI はこちら)。
# --verify なら、.NET で動かした同じ lub-gen と出力 (gen-api の生成物と tcs の接続コード) が同じことも確かめる (要 dotnet)。
#
# Usage: scripts/build-lub-gen.sh [--verify] [build-dir]   (既定 build-release-linux)
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

host_src=third_party/tcs/SelfHost
if [[ -x "$out/tcs$exe" ]]; then
  tcs_c=("$out/tcs$exe" c)
else
  dotnet build third_party/tcs/tcs2c -c Release -nologo -v q -o "$work/tcs2c" > /dev/null
  tcs_c=(dotnet "$work/tcs2c/tcs2c.dll")
  # lub-gen tcs は tcs と同じく lib/TinySystem を実行ファイルの dir から読む
  mkdir -p "$out/lib/TinySystem"
  cp third_party/tcs/TinySystem/Linq.cs third_party/tcs/TinySystem/ValueTuple.cs "$out/lib/TinySystem/"
fi
sources=("$host_src"/Compiler/*.cs tools/lub-gen/*.cs)
"${tcs_c[@]}" --lib --ref "$host_src/Tool/TcsHost.cs" "${sources[@]}" -o "$work/lub-gen.c"

# 浮動小数の規則 (tcs の il-spec 付録 B)。-fexcess-precision=standard を受けない compiler では省く (third_party/tcs/SelfHost/native.sh と同じ)
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
    fail=1
  fi
}
for verb in header lua docs facade facade-lubx model; do
  "$native" "$verb" -o "$work/native.$verb"
  "${dotnet_gen[@]}" "$verb" -o "$work/dotnet.$verb"
  same "$work/native.$verb" "$work/dotnet.$verb" "lub-gen $verb"
done
lubx_args=()
for source in cs-lib/lub/Math.cs cs-lib/lub/Bindings.cs cs-lib/lubx/*.cs samples/13_sprites/Sprites13.cs; do
  lubx_args+=(--source "$source")
done
# lub-gen tcs は lib/TinySystem を実行ファイルの dir から読む (tcs と同じ)
"$native" tcs --stub cs-lib/lub_stub.cs "${lubx_args[@]}" -o "$work/native.tcs"
"${dotnet_gen[@]}" tcs --stub cs-lib/lub_stub.cs "${lubx_args[@]}" -o "$work/dotnet.tcs"
same "$work/native.tcs" "$work/dotnet.tcs" "lub-gen tcs (cs-lib + 13_sprites)"
exit $fail
