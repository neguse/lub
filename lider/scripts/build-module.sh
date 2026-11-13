#!/usr/bin/env bash
# lider 自身を tcs VM の bytecode (Lider.tcsb) にする。
#   scripts/build-module.sh <out.tcsb>
# player に .csproj でなく .tcsb を渡して lider を動かすとき (web の page。web/lider/main.ts) に使う。
# 入力は src/ の *.cs (*.ref.cs は参照) と lub の cs-lib の実装。
# 要るもの: dotnet。
# env: LUB_TCS tcs の起動コマンド (既定: tcs/ の Transpiler を dotnet で)
set -euo pipefail
[ $# -eq 1 ] || {
  echo "usage: $0 <out.tcsb>" >&2
  exit 2
}
out="$1"
root="$(cd "$(dirname "$0")/.." && pwd)"
lub="$(cd "$root/.." && pwd)"
if [ -z "${LUB_TCS:-}" ]; then
  dll="$lub/tcs/Transpiler/bin/Release/net10.0/Transpiler.dll"
  [ -f "$dll" ] || dotnet build "$lub/tcs/Transpiler" -c Release >/dev/null
  LUB_TCS="dotnet $dll"
fi
mapfile -t sources < <(find "$root/src" -name '*.cs' ! -name '*.ref.cs' ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/.lub/*' | sort)
mapfile -t cslib < <(find "$lub/cs-lib" -name '*.cs' ! -name lub_stub.cs | sort)
mkdir -p "$(dirname "$out")"
# shellcheck disable=SC2086
$LUB_TCS vm "${sources[@]}" "${cslib[@]}" --ref "$lub/cs-lib/lub_stub.cs" --ref "$root/src/Jamo.ref.cs" --ref "$root/src/Fs.ref.cs" --export "Lider.*" -o "$out"
echo "wrote $out"
