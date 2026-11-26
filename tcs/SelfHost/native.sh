#!/bin/bash
# Converts tcs (src/tcs: Compiler, CBackend, VmBackend, Tool) to C with tcs2c and builds the native tcs.
# Output goes to SelfHost/out/native/ (the runtime/ and lib/ files shipped with tcs go there too; not checked into git).
# Checks that the built tcs produces the same output as the .NET tcs: check diagnostics and exit codes for samples and tcs itself,
# C for samples (tcs c), tcs VM .tcsb for samples and tcs itself (tcs vm), and reload batches (tcs vm --reload-from).
# Also checks that the native tcs produces its own C (tcs.c) identically (fixed point).
# CC selects the C compiler (default cc. GNU C statement expressions are used, so MSVC's cl cannot be used. On Windows, clang).
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
DOTNET_TCS=(dotnet "$ROOT/Transpiler/bin/Release/net11.0/Transpiler.dll")
TCS2C=(dotnet "$ROOT/tcs2c/bin/Release/net11.0/tcs2c.dll")
TCS2VM=(dotnet "$ROOT/tcsvm/Emitter/bin/Release/net11.0/tcs2vm.dll")
# Runtime C that tcs c embeds into the generated C
for d in core math fmt model utf8; do
  mkdir -p "$OUT/runtime/$d"
  cp "$ROOT/../src/tcs-runtime/$d"/*.c "$ROOT/../src/tcs-runtime/$d"/*.h "$OUT/runtime/$d/"
done
cp "$ROOT/TinySystem/Linq.cs" "$ROOT/TinySystem/ValueTuple.cs" "$OUT/lib/TinySystem/"

cd "$ROOT"
SRC=(../src/tcs/Compiler/*.cs ../src/tcs/CBackend/*.cs ../src/tcs/VmBackend/*.cs)
for f in ../src/tcs/Tool/*.cs; do
  [ "$(basename "$f")" = TcsHost.cs ] || SRC+=("$f")
done
ARGS=(--ref ../src/tcs/Tool/TcsHost.cs "${SRC[@]}")
"${TCS2C[@]}" --lib "${ARGS[@]}" -o "$OUT/tcs.c"

# Floating-point rules (il-spec Appendix B). Omitted for compilers that do not accept -fexcess-precision=standard
FLAGS=(-O2 -ffp-contract=off -fwrapv)
printf 'int main(void){return 0;}\n' > "$OUT/check/probe.c"
if "$CC" -fexcess-precision=standard -Werror -c "$OUT/check/probe.c" -o "$OUT/check/probe.o" >/dev/null 2>&1; then
  FLAGS+=(-fexcess-precision=standard)
fi
"$CC" "${FLAGS[@]}" -I "$OUT" ../src/tcs/Native/host.c -o "$OUT/tcs$EXE" ${LIBM[@]+"${LIBM[@]}"}
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

# Compares check diagnostics and exit codes (stdout, stderr, and the exit code in one file)
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

# samples: check, C (tcs c), tcs VM .tcsb (tcs vm; exports the static methods of the entry class)
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

# check error diagnostics
printf 'public class Bad { public static int F() { try { return 1; } finally { } } }\n' > SelfHost/out/native/check/Bad.cs
check_same bad SelfHost/out/native/check/Bad.cs

# check of tcs itself
start=$(date +%s)
check_same self "${ARGS[@]}"
echo "  tcs check itself: $(( $(date +%s) - start )) s"

# .tcsb of tcs itself (functions reachable from the CLI entry point)
"${TCS2VM[@]}" --export 'TcsSelf.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.dotnet.tcsb" 2>/dev/null
start=$(date +%s)
"$NATIVE" vm --export 'TcsSelf.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.native.tcsb" 2>/dev/null
echo "  tcs vm itself: $(( $(date +%s) - start )) s"
same "$OUT/check/self.dotnet.tcsb" "$OUT/check/self.native.tcsb" "tcs vm itself"

# tcs VM .tcsb. The digest kernel (entry is Main)
for k in particles:Particles spawn_churn:SpawnChurn sprite_update:SpriteUpdate; do
  file="Transpiler.Tests/DigestKernels/${k%%:*}.cs"
  "${TCS2VM[@]}" --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.native.tcsb" 2>/dev/null
  same "$OUT/check/${k%%:*}.dotnet.tcsb" "$OUT/check/${k%%:*}.native.tcsb" "tcs vm $file"
done

# Reload module and batch (tcs vm --reload-from)
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
