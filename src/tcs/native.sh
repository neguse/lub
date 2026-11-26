#!/bin/bash
# Builds the C tcs (src/tcs/build-native.sh) into src/tcs/out/native/ (not checked into git) and checks it against the .NET tcs.
# The built tcs must produce the same output as the .NET tcs: check diagnostics and exit codes for samples and tcs itself,
# C for samples (tcs c), tcs VM .tcsb for samples and tcs itself (tcs vm), and reload batches (tcs vm --reload-from).
# Also checks that the C tcs produces its own C (tcs.c) identically (fixed point).
# CC selects the C compiler (see build-native.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/src/tcs/out/native"
EXE=""
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) EXE=".exe" ;;
esac
mkdir -p "$OUT/check"
unset TCS_HOME

# The .NET tcs (scripts/tcs.sh). Built once here unless the caller passes LUB_TCS
LUB_TCS="${LUB_TCS:-$("$ROOT/scripts/build-tcs-dotnet.sh")}"
export LUB_TCS
DOTNET_TCS=("$ROOT/scripts/tcs.sh")

cd "$ROOT"
src/tcs/build-native.sh "$OUT"
mapfile -t ARGS < <(src/tcs/sources.sh)
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
  f="tests/tcs/samples/$name.cs"
  refs=()
  [ "$name" = host_api_game ] && refs=(--ref tests/tcs/samples/host_api_stub.cs)
  check_same "$name" "$f" ${refs[@]+"${refs[@]}"}
  "${DOTNET_TCS[@]}" c "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.dotnet.c" 2>/dev/null
  "$NATIVE" c "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.c" 2>/dev/null
  same "$OUT/check/$name.dotnet.c" "$OUT/check/$name.native.c" "tcs c $f"
  "${DOTNET_TCS[@]}" vm --export "$cls.*" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --export "$cls.*" "$f" ${refs[@]+"${refs[@]}"} -o "$OUT/check/$name.native.tcsb" 2>/dev/null
  same "$OUT/check/$name.dotnet.tcsb" "$OUT/check/$name.native.tcsb" "tcs vm $f"
done

# check error diagnostics
printf 'public class Bad { public static int F() { try { return 1; } finally { } } }\n' > src/tcs/out/native/check/Bad.cs
check_same bad src/tcs/out/native/check/Bad.cs

# check of tcs itself
start=$(date +%s)
check_same self "${ARGS[@]}"
echo "  tcs check itself: $(( $(date +%s) - start )) s"

# .tcsb of tcs itself (functions reachable from the CLI entry point)
"${DOTNET_TCS[@]}" vm --export 'Tcs.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.dotnet.tcsb" 2>/dev/null
start=$(date +%s)
"$NATIVE" vm --export 'Tcs.Cli.*' "${ARGS[@]}" -o "$OUT/check/self.native.tcsb" 2>/dev/null
echo "  tcs vm itself: $(( $(date +%s) - start )) s"
same "$OUT/check/self.dotnet.tcsb" "$OUT/check/self.native.tcsb" "tcs vm itself"

# tcs VM .tcsb. The digest kernel (entry is Main)
for k in particles:Particles spawn_churn:SpawnChurn sprite_update:SpriteUpdate; do
  file="tests/tcs/DigestKernels/${k%%:*}.cs"
  "${DOTNET_TCS[@]}" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.dotnet.tcsb" 2>/dev/null
  "$NATIVE" vm --entry "${k##*:}" "$file" -o "$OUT/check/${k%%:*}.native.tcsb" 2>/dev/null
  same "$OUT/check/${k%%:*}.dotnet.tcsb" "$OUT/check/${k%%:*}.native.tcsb" "tcs vm $file"
done

# Reload module and batch (tcs vm --reload-from)
HOST_TESTS=tests/tcs-vm/host
"${DOTNET_TCS[@]}" vm --export 'Game.*' --reload-from "$HOST_TESTS/ReloadV1.cs.txt" "$HOST_TESTS/ReloadV2.cs.txt" \
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
