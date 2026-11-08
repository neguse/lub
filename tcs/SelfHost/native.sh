#!/bin/bash
# tcs2c で tcs (SelfHost/Compiler、SelfHost/CBackend、SelfHost/VmBackend、SelfHost/Tool) を C にし、ネイティブの tcs を作る。
# 出力は SelfHost/out/native/ (tcs と一緒に配るファイル runtime/ と lib/ も置く。git に入れない)。
# 作った tcs が .NET の tcs と同じ出力を出すことを確かめる: samples と tcs 自身の check の診断と終了コード、
# samples の C (tcs c)、samples と tcs 自身の tcs VM の .tcsb (tcs vm)、reload の batch (tcs vm --reload-from)。
# ネイティブの tcs が自分の C (tcs.c) を同じに作れること (固定点) も確かめる。
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

# check の診断と終了コードを比べる (stdout と stderr と終了コードを 1 つのファイルに)
check_same() {
  local name="$1"
  shift
  set +e
  "${DOTNET_TCS[@]}" check "$@" >"$OUT/check/$name.check.dotnet.txt" 2>&1
  echo "exit $?" >>"$OUT/check/$name.check.dotnet.txt"
  "$NATIVE" check "$@" >"$OUT/check/$name.check.native.txt" 2>&1
  echo "exit $?" >>"$OUT/check/$name.check.native.txt"
  set -e
  same "$OUT/check/$name.check.dotnet.txt" "$OUT/check/$name.check.native.txt" "tcs check $name"
}

# samples: check、C (tcs c)、tcs VM の .tcsb (tcs vm。entry の class の static method を export する)
for pair in hello:Hello game:Battle inventory:Game entity:EntitySample statemachine:StateMachineSample collision:CollisionSample host_api_game:HostApiSample; do
  name="${pair%%:*}"
  cls="${pair##*:}"
  f="samples/$name.cs"
  refs=()
  [ "$name" = host_api_game ] && refs=(--ref samples/host_api_stub.cs)
  check_same "$name" "$f" ${refs[@]+"${refs[@]}"}
  "${TCS2C[@]}" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.tcs2c.c" 2>/dev/null
  "$NATIVE" c "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.c" 2>/dev/null
  same "$OUT/check/$name.tcs2c.c" "$OUT/check/$name.native.c" "tcs c $f"
  "${TCS2VM[@]}" --export "$cls.*" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --export "$cls.*" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.tcsb" 2>/dev/null
  same "$OUT/check/$name.dotnet.tcsb" "$OUT/check/$name.native.tcsb" "tcs vm $f"
done

# check の誤りの診断
printf 'public class Bad { public static int F() { try { return 1; } finally { } } }\n' > SelfHost/out/native/check/Bad.cs
check_same bad SelfHost/out/native/check/Bad.cs

# tcs 自身の check
start=$(date +%s)
check_same self "${ARGS[@]}"
echo "  tcs check itself: $(( $(date +%s) - start )) s"

# tcs 自身の .tcsb (CLI の入口から届く関数)
"${TCS2VM[@]}" --export 'TcsSelf.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.dotnet.tcsb" 2>/dev/null
start=$(date +%s)
"$NATIVE" vm --export 'TcsSelf.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.native.tcsb" 2>/dev/null
echo "  tcs vm itself: $(( $(date +%s) - start )) s"
same "$OUT/check/self.dotnet.tcsb" "$OUT/check/self.native.tcsb" "tcs vm itself"

# tcs VM の .tcsb。digest の kernel (entry は Main)
for k in particles:Particles spawn_churn:SpawnChurn sprite_update:SpriteUpdate; do
  file="Transpiler.Tests/DigestKernels/${k%%:*}.cs"
  "${TCS2VM[@]}" --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.native.tcsb" 2>/dev/null
  same "$OUT/check/${k%%:*}.dotnet.tcsb" "$OUT/check/${k%%:*}.native.tcsb" "tcs vm $file"
done

# reload の module と batch (tcs vm --reload-from)
HOST_TESTS=tcsvm/tests/host
"${TCS2VM[@]}" --export 'Game.*' --reload-from "$HOST_TESTS/ReloadV1.cs.txt" "$HOST_TESTS/ReloadV2.cs.txt" \
  -o "$OUT/check/reload.dotnet.tcsb" --batch "$OUT/check/reload.dotnet.batch" 2>/dev/null
"$NATIVE" vm --export 'Game.*' --reload-from "$HOST_TESTS/ReloadV1.cs.txt" "$HOST_TESTS/ReloadV2.cs.txt" \
  -o "$OUT/check/reload.native.tcsb" --batch "$OUT/check/reload.native.batch" 2>/dev/null
same "$OUT/check/reload.dotnet.batch" "$OUT/check/reload.native.batch" "tcs vm --reload-from (batch)"

start=$(date +%s)
"$NATIVE" c --lib "${ARGS[@]}" -o "$OUT/check/self.native.c" 2>/dev/null
echo "  tcs c itself: $(( $(date +%s) - start )) s"
same "$OUT/tcs.c" "$OUT/check/self.native.c" "tcs c itself (fixed point)"

if [ "$fail" != 0 ]; then
  echo "native tcs differs from the .NET tcs" >&2
  exit 1
fi
echo "native tcs matches the .NET tcs"
