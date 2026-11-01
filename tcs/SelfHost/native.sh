#!/bin/bash
# tcs2c で tcs (SelfHost/Compiler と SelfHost/Tool) を C にし、ネイティブの tcs を作る。
# 出力は SelfHost/out/native/ (tcs と一緒に配るファイル runtime/ と lib/ も置く。git に入れない)。
# 作った tcs が .NET の tcs と同じ出力を出すことを確かめる: samples の変換、check の終了コード、--snapshot、tcs 自身の変換。
# CC で C compiler を選ぶ (既定は cc。GNU C の文の式を使うので MSVC の cl は使えない。Windows は clang)。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/SelfHost/out/native"
CC="${CC:-cc}"
EXE=""
LIBM=(-lm)
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    EXE=".exe"
    LIBM=()
    ;;
esac
mkdir -p "$OUT/runtime" "$OUT/lib/TinySystem" "$OUT/check"
unset TCS_HOME

dotnet build "$ROOT/Transpiler" -c Release -v q -nologo >/dev/null
dotnet build "$ROOT/tcs2c" -c Release -v q -nologo >/dev/null
DOTNET_TCS=(dotnet "$ROOT/Transpiler/bin/Release/net10.0/Transpiler.dll")
TCS2C=(dotnet "$ROOT/tcs2c/bin/Release/net10.0/tcs2c.dll")
cp "$ROOT/runtime/tinysystem.lua" "$ROOT/runtime/module_registry.lua" "$OUT/runtime/"
cp "$ROOT/TinySystem/Linq.cs" "$ROOT/TinySystem/ValueTuple.cs" "$OUT/lib/TinySystem/"

cd "$ROOT"
SRC=(SelfHost/Compiler/*.cs)
for f in SelfHost/Tool/*.cs; do
  [ "$(basename "$f")" = TcsHost.cs ] || SRC+=("$f")
done
ARGS=(--ref SelfHost/Tool/TcsHost.cs "${SRC[@]}")
"${TCS2C[@]}" --lib "${ARGS[@]}" -o "$OUT/tcs.c"

# 浮動小数の規則 (il-spec 付録 B)。-fexcess-precision=standard を受けない compiler では省く
FLAGS=(-O2 -ffp-contract=off -fwrapv)
printf 'int main(void){return 0;}\n' > "$OUT/check/probe.c"
if "$CC" -fexcess-precision=standard -Werror -c "$OUT/check/probe.c" -o "$OUT/check/probe.o" >/dev/null 2>&1; then
  FLAGS+=(-fexcess-precision=standard)
fi
"$CC" "${FLAGS[@]}" -I "$OUT" SelfHost/Native/host.c -o "$OUT/tcs$EXE" ${LIBM[@]+"${LIBM[@]}"}
NATIVE="$OUT/tcs$EXE"
echo "native: $NATIVE ($("$NATIVE" --version))"

fail=0
same() {
  if cmp -s "$1" "$2"; then
    echo "  same: $3"
  else
    echo "  DIFFERENT: $3" >&2
    diff "$1" "$2" | head -20 >&2 || true
    fail=1
  fi
}

# samples の変換 (.NET の tcs が変換できるものだけ比べる)
for f in samples/*.cs; do
  name="$(basename "$f" .cs)"
  [ "$name" = host_api_stub ] && continue
  refs=()
  [ "$name" = host_api_game ] && refs=(--ref samples/host_api_stub.cs)
  if "${DOTNET_TCS[@]}" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.dotnet.lua" 2>/dev/null; then
    "$NATIVE" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.lua" 2>/dev/null
    same "$OUT/check/$name.dotnet.lua" "$OUT/check/$name.native.lua" "$f"
  fi
done

# check の終了コードと診断
mkdir -p SelfHost/out/native/check && printf 'public class Bad { public static int F() { try { return 1; } finally { } } }\n' > SelfHost/out/native/check/Bad.cs
set +e
"${DOTNET_TCS[@]}" check SelfHost/out/native/check/Bad.cs >"$OUT/check/bad.dotnet.txt" 2>&1
dotnet_code=$?
"$NATIVE" check SelfHost/out/native/check/Bad.cs >"$OUT/check/bad.native.txt" 2>&1
native_code=$?
set -e
if [ "$dotnet_code" = "$native_code" ] && [ "$native_code" != 0 ]; then
  echo "  same: check exit code $native_code"
else
  echo "  DIFFERENT: check exit code (dotnet $dotnet_code, native $native_code)" >&2
  fail=1
fi
same "$OUT/check/bad.dotnet.txt" "$OUT/check/bad.native.txt" "check diagnostics"

# module の snapshot
"${DOTNET_TCS[@]}" --snapshot --entry Battle samples/game.cs -o "$OUT/check/snap.dotnet.lua" 2>/dev/null
"$NATIVE" --snapshot --entry Battle samples/game.cs -o "$OUT/check/snap.native.lua" 2>/dev/null
same "$OUT/check/snap.dotnet.lua" "$OUT/check/snap.native.lua" "--snapshot"

# tcs 自身
"${DOTNET_TCS[@]}" "${ARGS[@]}" -o "$OUT/check/self.dotnet.lua" 2>/dev/null
start=$(date +%s)
"$NATIVE" "${ARGS[@]}" -o "$OUT/check/self.native.lua" 2>/dev/null
echo "  tcs itself: $(( $(date +%s) - start )) s"
same "$OUT/check/self.dotnet.lua" "$OUT/check/self.native.lua" "tcs itself"

if [ "$fail" != 0 ]; then
  echo "native tcs differs from the .NET tcs" >&2
  exit 1
fi
echo "native tcs matches the .NET tcs"
