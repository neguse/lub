#!/bin/bash
# tcs2c で tcs (SelfHost/Compiler、SelfHost/CBackend、SelfHost/VmBackend、SelfHost/Tool) を C にし、ネイティブの tcs を作る。
# 出力は SelfHost/out/native/ (tcs と一緒に配るファイル runtime/ と lib/ も置く。git に入れない)。
# 作った tcs が .NET の tcs と同じ出力を出すことを確かめる: samples の変換、check の終了コード、--snapshot、tcs 自身の変換。
# C の出力 (tcs c) も比べ、ネイティブの tcs が自分の C (tcs.c) を同じに作れること (固定点) を確かめる。
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
dotnet build "$ROOT/tcsvm/Emitter/tcs2vm.csproj" -c Release -v q -nologo >/dev/null
DOTNET_TCS=(dotnet "$ROOT/Transpiler/bin/Release/net10.0/Transpiler.dll")
TCS2C=(dotnet "$ROOT/tcs2c/bin/Release/net10.0/tcs2c.dll")
TCS2VM=(dotnet "$ROOT/tcsvm/Emitter/bin/Release/net10.0/tcs2vm.dll")
cp "$ROOT/runtime/tinysystem.lua" "$ROOT/runtime/module_registry.lua" "$OUT/runtime/"
# tcs c が生成 C に埋め込む runtime の C
for d in core math fmt model utf8; do
  mkdir -p "$OUT/runtime/$d"
  cp "$ROOT/runtime/$d"/*.c "$ROOT/runtime/$d"/*.h "$OUT/runtime/$d/"
done
cp "$ROOT/TinySystem/Linq.cs" "$ROOT/TinySystem/ValueTuple.cs" "$OUT/lib/TinySystem/"

cd "$ROOT"
SRC=(SelfHost/Compiler/*.cs SelfHost/CBackend/*.cs SelfHost/VmBackend/*.cs)
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

# 出力が入力と同じファイル (hard link) を指すときは書かずに止める (ファイルの同一性。Windows は volume とファイルの番号)
cp samples/hello.cs "$OUT/check/guard.cs"
rm -f "$OUT/check/guard_link.cs"
if ln "$OUT/check/guard.cs" "$OUT/check/guard_link.cs" 2>/dev/null; then
  set +e
  "$NATIVE" "$OUT/check/guard.cs" -o "$OUT/check/guard_link.cs" >"$OUT/check/guard.txt" 2>&1
  guard_code=$?
  set -e
  if [ "$guard_code" = 1 ] && grep -q "conflicts with input" "$OUT/check/guard.txt" && cmp -s samples/hello.cs "$OUT/check/guard.cs"; then
    echo "  same: output to a hard link of the input is refused"
  else
    echo "  DIFFERENT: output to a hard link of the input was not refused (exit $guard_code)" >&2
    cat "$OUT/check/guard.txt" >&2
    fail=1
  fi
fi

# C の出力 (tcs c)。samples と、tcs 自身 (tcs2c が作った tcs.c と同じなら固定点)
for name in hello game collision; do
  "${TCS2C[@]}" "samples/$name.cs" -o "$OUT/check/$name.tcs2c.c" 2>/dev/null
  "$NATIVE" c "samples/$name.cs" -o "$OUT/check/$name.native.c" 2>/dev/null
  same "$OUT/check/$name.tcs2c.c" "$OUT/check/$name.native.c" "tcs c samples/$name.cs"
done
# tcs VM の .tcsb (tcs vm)。.NET の tcs2vm と同じ byte 列
for k in particles:Particles spawn_churn:SpawnChurn sprite_update:SpriteUpdate; do
  file="Transpiler.Tests/DigestKernels/${k%%:*}.cs"
  "${TCS2VM[@]}" --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.native.tcsb" 2>/dev/null
  same "$OUT/check/${k%%:*}.dotnet.tcsb" "$OUT/check/${k%%:*}.native.tcsb" "tcs vm $file"
done

start=$(date +%s)
"$NATIVE" c --lib "${ARGS[@]}" -o "$OUT/check/self.native.c" 2>/dev/null
echo "  tcs c itself: $(( $(date +%s) - start )) s"
same "$OUT/tcs.c" "$OUT/check/self.native.c" "tcs c itself (fixed point)"

if [ "$fail" != 0 ]; then
  echo "native tcs differs from the .NET tcs" >&2
  exit 1
fi
echo "native tcs matches the .NET tcs"
